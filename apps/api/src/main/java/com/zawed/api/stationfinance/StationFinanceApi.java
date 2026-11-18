package com.zawed.api.stationfinance;

import java.time.Instant;
import java.time.LocalDate;
import java.util.List;
import java.util.UUID;

import jakarta.validation.constraints.Max;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Positive;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import com.zawed.api.ledger.Ledger.Direction;
import com.zawed.api.ledger.Ledger.SourceType;
import com.zawed.api.stationfinance.BankAccounts.AccountKind;
import com.zawed.api.stationfinance.BankAccounts.BankAccount;
import com.zawed.api.stationfinance.BankAccounts.Details;
import com.zawed.api.stationfinance.PayableBook.Position;
import com.zawed.api.stationfinance.PayableHolds.HeldPayable;
import com.zawed.api.stationfinance.Payouts.Payout;
import com.zawed.api.stationfinance.Reversals.Reversal;
import com.zawed.api.stationfinance.StationStatements.Line;
import com.zawed.api.stationfinance.StationStatements.Period;

/**
 * Request and response bodies of the station finance endpoints. Money is integer piastres. No
 * response carries a bank account number: only its kind and last four characters.
 */
final class StationFinanceApi {

	private StationFinanceApi() {
	}

	// ----------------------------------------------------------------------------- bank accounts

	record BankAccountChangeBody(
			@NotBlank @Size(max = 64) @Schema(description = "IBAN or account number; spaces and dashes are ignored. Never returned.") String accountNumber,
			@NotBlank @Size(max = 200) String bankName,
			@NotBlank @Size(max = 200) @Schema(description = "The account holder, as on the bank's letter") String holderName,
			@NotBlank @Size(max = 100) @Schema(description = "The reference of the bank's letter that confirms the account") String bankLetterReference) {

		/** The number must not reach a log line through a record's default {@code toString}. */
		@Override
		public String toString() {
			return "BankAccountChangeBody[account number hidden, bankName=" + bankName + "]";
		}

	}

	record RejectBankAccountChangeRequest(@NotBlank @Size(max = 500) String reason) {
	}

	record BankAccountResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) AccountKind accountKind,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The last four characters of the IBAN or account number; the rest is never returned") String accountLast4,
			@Schema(requiredMode = RequiredMode.REQUIRED) String bankName,
			@Schema(requiredMode = RequiredMode.REQUIRED) String holderName,
			@Schema(requiredMode = RequiredMode.REQUIRED) String bankLetterReference,
			@Schema(requiredMode = RequiredMode.REQUIRED) BankAccounts.Status status,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID requestedBy,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant requestedAt, UUID decidedBy, Instant decidedAt,
			String decisionReason,
			@Schema(description = "No payout may go to the station before this instant; set when the change is approved") Instant payoutsHeldUntil,
			Instant supersededAt) {

		static BankAccountResponse of(BankAccount a) {
			return a == null ? null
					: new BankAccountResponse(a.id(), a.stationId(), a.accountKind(), a.accountLast4(), a.bankName(),
							a.holderName(), a.bankLetterReference(), a.status(), a.requestedBy(), a.requestedAt(),
							a.decidedBy(), a.decidedAt(), a.decisionReason(), a.payoutsHeldUntil(), a.supersededAt());
		}

	}

	record StationBankDetailsResponse(@Schema(description = "The verified account payouts go to; null when there is none") BankAccountResponse active,
			@Schema(description = "A change waiting for a second person; null when there is none") BankAccountResponse pending) {

		static StationBankDetailsResponse of(Details details) {
			return new StationBankDetailsResponse(BankAccountResponse.of(details.active()),
					BankAccountResponse.of(details.pending()));
		}

	}

	// ----------------------------------------------------------------------------------- payouts

	record RecordPayoutRequest(@NotNull @Positive @Max(Payouts.MAX_AMOUNT_PIASTRES) @Schema(description = "Piastres") Long amountPiastres,
			@NotBlank @Size(max = 64) @Schema(description = "The bank's transfer reference; one reference pays once") String bankReference,
			@NotNull @Schema(description = "The transfer's value date, at most 7 days ahead") LocalDate valueDate,
			@Size(max = 500) String note) {
	}

	record CancelPayoutRequest(@NotBlank @Size(max = 500) String reason) {
	}

	record PayoutResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID operatorId,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The verified bank account it was recorded for") UUID bankAccountId,
			@Schema(requiredMode = RequiredMode.REQUIRED) long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) String bankReference,
			@Schema(requiredMode = RequiredMode.REQUIRED) LocalDate valueDate, String note,
			@Schema(requiredMode = RequiredMode.REQUIRED) Payouts.Status status,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Above the threshold: a second person must approve before it posts") boolean approvalRequired,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The threshold in force when it was recorded") long approvalThresholdPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID requestedBy,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant requestedAt,
			@Schema(description = "Who approved or cancelled it; null for a payout that posted at once") UUID decidedBy,
			Instant decidedAt, String decisionReason, Instant postedAt, UUID ledgerTransactionId) {

		static PayoutResponse of(Payout p) {
			return new PayoutResponse(p.id(), p.stationId(), p.operatorId(), p.bankAccountId(), p.amountPiastres(),
					p.bankReference(), p.valueDate(), p.note(), p.status(), p.approvalRequired(),
					p.approvalThresholdPiastres(), p.requestedBy(), p.requestedAt(), p.decidedBy(), p.decidedAt(),
					p.decisionReason(), p.postedAt(), p.ledgerTransactionId());
		}

	}

	// ---------------------------------------------------------------------------- held payables

	record ReleaseHoldRequest(@NotBlank @Size(max = 500) String reason) {
	}

	record HeldPayableResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID fuelingId,
			@Schema(requiredMode = RequiredMode.REQUIRED) String reference,
			@Schema(requiredMode = RequiredMode.REQUIRED) String plate,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID operatorId,
			@Schema(requiredMode = RequiredMode.REQUIRED) long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant evaluatedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant receivedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "What offline sync found (B5 contract §4)") List<String> conflicts,
			@Schema(requiredMode = RequiredMode.REQUIRED) PayableHolds.State state, UUID releasedBy, Instant releasedAt,
			String releaseReason) {

		static HeldPayableResponse of(HeldPayable h) {
			return new HeldPayableResponse(h.fuelingId(), h.reference(), h.plate(), h.stationId(), h.operatorId(),
					h.amountPiastres(), h.evaluatedAt(), h.receivedAt(), h.conflicts(), h.state(), h.releasedBy(),
					h.releasedAt(), h.releaseReason());
		}

	}

	// --------------------------------------------------------------------------------- statement

	record StationPayableResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "What the platform owes the station; negative when the station owes the platform") long balancePiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Offline fuelings held for Operations, not released and not reversed") long heldPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "balance − held; may be negative") long availablePiastres,
			@Schema(description = "Payouts recorded and waiting for approval; a new payout may take at most available − this. Operations only: null in the station portal") Long pendingPayoutsPiastres) {

		static StationPayableResponse of(UUID stationId, Position p, boolean operations) {
			return new StationPayableResponse(stationId, p.balancePiastres(), p.heldPiastres(), p.availablePiastres(),
					operations ? p.pendingPayoutsPiastres() : null);
		}

	}

	record StatementFuelingLink(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String reference,
			@Schema(requiredMode = RequiredMode.REQUIRED) String plate,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Its payable was held at sync") boolean payableHeld,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Operations released the hold") boolean holdReleased) {
	}

	record StatementPayoutLink(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String bankReference,
			@Schema(requiredMode = RequiredMode.REQUIRED) LocalDate valueDate) {
	}

	record StationStatementEntryResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant postedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "CREDIT raises what the station is owed (a fueling), DEBIT lowers it (a payout, a reversal)") Direction direction,
			@Schema(requiredMode = RequiredMode.REQUIRED) long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The payable right after this entry; negative when the station owes") long balanceAfterPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) SourceType sourceType,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The fueling, payout or reversal id") UUID sourceId,
			String reference, String description,
			@Schema(description = "The fueling of a FUELING entry, or the fueling a REVERSAL entry reverses") StatementFuelingLink fueling,
			@Schema(description = "The payout of a PAYOUT entry") StatementPayoutLink payout) {

		static StationStatementEntryResponse of(Line l) {
			return new StationStatementEntryResponse(l.id(), l.postedAt(), l.direction(), l.amountPiastres(),
					l.balanceAfterPiastres(), l.sourceType(), l.sourceId(), l.reference(), l.description(),
					l.fueling() == null ? null
							: new StatementFuelingLink(l.fueling().id(), l.fueling().reference(), l.fueling().plate(),
									l.fueling().payableHeld(), l.fueling().holdReleased()),
					l.payout() == null ? null
							: new StatementPayoutLink(l.payout().id(), l.payout().bankReference(), l.payout().valueDate()));
		}

	}

	record StationStatementSummaryResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(description = "Start of the period, inclusive; null from the first entry") Instant from,
			@Schema(description = "End of the period, exclusive; null up to now") Instant to,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The payable just before the period") long openingPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Fuelings credited in the period") long creditsPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Payouts and reversals debited in the period") long debitsPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "opening + credits − debits") long closingPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) int entries,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The station's position now, whatever the period") StationPayableResponse now) {

		static StationStatementSummaryResponse of(Period p, boolean operations) {
			return new StationStatementSummaryResponse(p.stationId(), p.from(), p.to(), p.openingPiastres(),
					p.creditsPiastres(), p.debitsPiastres(), p.closingPiastres(), p.entries(),
					StationPayableResponse.of(p.stationId(), p.position(), operations));
		}

	}

	// --------------------------------------------------------------------------------- reversals

	record RequestReversalBody(@NotBlank @Size(max = 500) String reason) {
	}

	record RejectReversalRequest(@NotBlank @Size(max = 500) String reason) {
	}

	record ReversalResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID fuelingId,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The fueling's receipt reference") String fuelingReference,
			@Schema(requiredMode = RequiredMode.REQUIRED) String plate,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) String reason,
			@Schema(requiredMode = RequiredMode.REQUIRED) Reversals.Status status,
			@Schema(description = "Operations only") UUID requestedBy,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant requestedAt,
			@Schema(description = "Operations only") UUID decidedBy, Instant decidedAt,
			@Schema(description = "Operations only") String decisionReason, UUID ledgerTransactionId) {

		/** As Operations reads it. */
		static ReversalResponse of(Reversal r) {
			return new ReversalResponse(r.id(), r.fuelingId(), r.fuelingReference(), r.plate(), r.companyId(),
					r.stationId(), r.amountPiastres(), r.reason(), r.status(), r.requestedBy(), r.requestedAt(),
					r.decidedBy(), r.decidedAt(), r.decisionReason(), r.ledgerTransactionId());
		}

		/** As a company or a station reads it: what happened and why, not which staff member did it. */
		static ReversalResponse forPortal(Reversal r) {
			return new ReversalResponse(r.id(), r.fuelingId(), r.fuelingReference(), r.plate(), r.companyId(),
					r.stationId(), r.amountPiastres(), r.reason(), r.status(), null, r.requestedAt(), null, r.decidedAt(),
					null, r.ledgerTransactionId());
		}

	}

}
