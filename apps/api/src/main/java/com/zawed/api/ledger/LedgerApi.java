package com.zawed.api.ledger;

import java.time.Instant;
import java.time.LocalDate;
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
import com.zawed.api.ledger.Reconciliation.Finding;
import com.zawed.api.ledger.Reconciliation.FindingKind;
import com.zawed.api.ledger.Reconciliation.Run;
import com.zawed.api.ledger.Reconciliation.Trigger;
import com.zawed.api.ledger.TopUps.Status;
import com.zawed.api.ledger.TopUps.TopUp;
import com.zawed.api.ledger.Wallets.StatementEntry;
import com.zawed.api.ledger.Wallets.WalletView;

/** Request and response bodies of the wallet, top-up and reconciliation endpoints. Money is integer piastres. */
final class LedgerApi {

	private LedgerApi() {
	}

	record TopUpRequestBody(@NotNull @Positive @Max(TopUps.MAX_AMOUNT_PIASTRES) @Schema(description = "Piastres") Long amountPiastres,
			@NotBlank @Size(max = 64) @Schema(description = "The bank's transfer reference") String bankReference,
			@NotNull @Schema(description = "Date of the transfer, not in the future") LocalDate transferDate,
			@Size(max = 500) String note) {
	}

	/** Step 2 of a top-up (V12): what Operations read on ZAWED's bank statement. */
	record VerifyTopUpRequest(
			@NotNull @Positive @Max(TopUps.MAX_AMOUNT_PIASTRES) @Schema(description = "The amount that actually arrived, in piastres. This, never the company's figure, is what will be credited") Long receivedAmountPiastres,
			@Size(max = 32) @Schema(description = "Which ZAWED account received it (default MAIN)") String receivingAccount,
			@Size(max = 64) @Schema(description = "The reference as it appears on the bank statement, when it differs from the requested one") String bankReference,
			@Schema(description = "The value date on the statement, when it differs from the requested one") LocalDate transferDate,
			@Size(max = 200) @Schema(description = "Where the evidence is: statement line, scan id, e-mail subject") String evidenceReference,
			@Size(max = 500) String note) {
	}

	/** A transfer that arrived without a company request, recorded and verified by Operations (V12). */
	record RecordTransferRequest(@NotNull @Positive @Max(TopUps.MAX_AMOUNT_PIASTRES) @Schema(description = "The amount that arrived, in piastres") Long amountPiastres,
			@Size(max = 32) @Schema(description = "Which ZAWED account received it (default MAIN)") String receivingAccount,
			@NotBlank @Size(max = 64) @Schema(description = "The reference on the bank statement") String bankReference,
			@NotNull @Schema(description = "Value date, not in the future") LocalDate transferDate,
			@Size(max = 200) String evidenceReference, @Size(max = 500) String note) {
	}

	record WalletAdjustmentRequest(@NotNull @Positive @Max(TopUps.MAX_AMOUNT_PIASTRES) @Schema(description = "A credit, in piastres") Long amountPiastres,
			@NotBlank @Size(max = 500) @Schema(description = "Why the wallet is credited without a bank transfer") String reason) {
	}

	record RejectWalletAdjustmentRequest(@NotBlank @Size(max = 500) String reason) {
	}

	record WalletAdjustmentResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId,
			@Schema(requiredMode = RequiredMode.REQUIRED) long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) String reason,
			@Schema(requiredMode = RequiredMode.REQUIRED) WalletAdjustments.Status status,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID requestedBy,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant requestedAt, UUID decidedBy, Instant decidedAt,
			String decisionReason, UUID ledgerTransactionId) {

		static WalletAdjustmentResponse of(WalletAdjustments.Adjustment a) {
			return new WalletAdjustmentResponse(a.id(), a.companyId(), a.amountPiastres(), a.reason(), a.status(),
					a.requestedBy(), a.requestedAt(), a.decidedBy(), a.decidedAt(), a.decisionReason(),
					a.ledgerTransactionId());
		}

	}

	record RejectTopUpRequest(@NotBlank @Size(max = 500) String reason) {
	}

	record WalletResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId,
			@Schema(requiredMode = RequiredMode.REQUIRED) long balancePiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) long lowBalanceThresholdPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "Balance below the company's threshold") boolean lowBalance,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant updatedAt) {

		static WalletResponse of(WalletView w) {
			return new WalletResponse(w.companyId(), w.balancePiastres(), w.lowBalanceThresholdPiastres(), w.lowBalance(),
					w.updatedAt());
		}

	}

	record StatementEntryResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant postedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "CREDIT adds to the wallet, DEBIT takes from it") Direction direction,
			@Schema(requiredMode = RequiredMode.REQUIRED) long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The wallet balance right after this entry") long balanceAfterPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) SourceType sourceType,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "The top-up, fueling or payout id") UUID sourceId,
			String reference, String description) {

		static StatementEntryResponse of(StatementEntry e) {
			return new StatementEntryResponse(e.id(), e.postedAt(), e.direction(), e.amountPiastres(),
					e.balanceAfterPiastres(), e.sourceType(), e.sourceId(), e.reference(), e.description());
		}

	}

	record TopUpResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID companyId,
			@Schema(requiredMode = RequiredMode.REQUIRED) TopUps.Origin origin,
			@Schema(requiredMode = RequiredMode.REQUIRED, description = "What the company said it transferred (for OPS_RECORDED, what Operations recorded). Never what is credited") long amountPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) String receivingAccount,
			@Schema(requiredMode = RequiredMode.REQUIRED) String bankReference,
			@Schema(requiredMode = RequiredMode.REQUIRED) LocalDate transferDate, String note,
			@Schema(requiredMode = RequiredMode.REQUIRED) Status status,
			@Schema(description = "Null when Operations recorded the transfer without a request") UUID requestedBy,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant requestedAt,
			@Schema(description = "What arrived, as verified on the bank statement: the amount credited on approval") Long verifiedAmountPiastres,
			UUID verifiedBy, Instant verifiedAt, String evidenceReference,
			@Schema(description = "Who approved, rejected or cancelled it") UUID decidedBy, Instant decidedAt,
			String decisionReason, UUID ledgerTransactionId) {

		static TopUpResponse of(TopUp t) {
			return new TopUpResponse(t.id(), t.companyId(), t.origin(), t.amountPiastres(), t.receivingAccount(),
					t.bankReference(), t.transferDate(), t.note(), t.status(), t.requestedBy(), t.requestedAt(),
					t.verifiedAmountPiastres(), t.verifiedBy(), t.verifiedAt(), t.evidenceReference(), t.decidedBy(),
					t.decidedAt(), t.decisionReason(), t.ledgerTransactionId());
		}

	}

	record ReconciliationRunResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) Trigger trigger, UUID triggeredBy,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant startedAt, Instant finishedAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) int walletsChecked,
			@Schema(requiredMode = RequiredMode.REQUIRED) int payablesChecked,
			@Schema(requiredMode = RequiredMode.REQUIRED) int findingsCount) {

		static ReconciliationRunResponse of(Run r) {
			return new ReconciliationRunResponse(r.id(), r.trigger(), r.triggeredBy(), r.startedAt(), r.finishedAt(),
					r.walletsChecked(), r.payablesChecked(), r.findingsCount());
		}

	}

	record ReconciliationFindingResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID runId,
			@Schema(description = "The wallet's company (BALANCE_MISMATCH, MISSING_WALLET); null for a station payable") UUID companyId,
			@Schema(description = "The payable's station (PAYABLE_MISMATCH, MISSING_PAYABLE); null for a wallet") UUID stationId,
			@Schema(requiredMode = RequiredMode.REQUIRED) FindingKind kind, Long cachedBalancePiastres,
			Long ledgerBalancePiastres,
			@Schema(description = "Cached minus ledger; positive means the cache shows more than the ledger") Long differencePiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static ReconciliationFindingResponse of(Finding f) {
			return new ReconciliationFindingResponse(f.id(), f.runId(), f.companyId(), f.stationId(), f.kind(),
					f.cachedBalancePiastres(), f.ledgerBalancePiastres(), f.differencePiastres(), f.createdAt());
		}

	}

}
