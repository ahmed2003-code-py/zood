package com.zawed.api.stationfinance;

import java.util.List;
import java.util.Objects;
import java.util.stream.Stream;

import org.springframework.stereotype.Component;

import com.zawed.api.identity.ActorDirectory;
import com.zawed.api.identity.Actors;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.Page;
import com.zawed.api.stationfinance.BankAccounts.BankAccount;
import com.zawed.api.stationfinance.BankAccounts.Details;
import com.zawed.api.stationfinance.Payouts.Payout;
import com.zawed.api.stationfinance.StationFinanceApi.BankAccountResponse;
import com.zawed.api.stationfinance.StationFinanceApi.PayoutResponse;
import com.zawed.api.stationfinance.StationFinanceApi.StationBankDetailsResponse;

/**
 * Bank account changes and payouts as the signed-in caller reads them: who asked and who decided,
 * by name for Operations and as ZAWED operations for a station ({@link ActorDirectory}, QA round 1).
 */
@Component
class StationFinanceViews {

	private final ActorDirectory people;

	StationFinanceViews(ActorDirectory people) {
		this.people = people;
	}

	StationBankDetailsResponse details(ZawedPrincipal viewer, Details details) {
		Actors actors = people.describe(viewer, Stream.of(details.active(), details.pending())
			.filter(Objects::nonNull)
			.flatMap((a) -> Stream.of(a.requestedBy(), a.decidedBy()))
			.filter(Objects::nonNull)
			.toList());
		return StationBankDetailsResponse.of(details, actors);
	}

	BankAccountResponse bankAccount(ZawedPrincipal viewer, BankAccount account) {
		return bankAccounts(viewer, List.of(account)).get(0);
	}

	Page<BankAccountResponse> bankAccounts(ZawedPrincipal viewer, Page<BankAccount> page) {
		return new Page<>(bankAccounts(viewer, page.items()), page.nextCursor());
	}

	private List<BankAccountResponse> bankAccounts(ZawedPrincipal viewer, List<BankAccount> accounts) {
		Actors actors = people.describe(viewer, accounts.stream()
			.flatMap((a) -> Stream.of(a.requestedBy(), a.decidedBy()))
			.filter(Objects::nonNull)
			.toList());
		return accounts.stream().map((a) -> BankAccountResponse.of(a, actors)).toList();
	}

	PayoutResponse payout(ZawedPrincipal viewer, Payout payout) {
		return payouts(viewer, List.of(payout)).get(0);
	}

	Page<PayoutResponse> payouts(ZawedPrincipal viewer, Page<Payout> page) {
		return new Page<>(payouts(viewer, page.items()), page.nextCursor());
	}

	private List<PayoutResponse> payouts(ZawedPrincipal viewer, List<Payout> payouts) {
		Actors actors = people.describe(viewer, payouts.stream()
			.flatMap((p) -> Stream.of(p.requestedBy(), p.decidedBy()))
			.filter(Objects::nonNull)
			.toList());
		return payouts.stream().map((p) -> PayoutResponse.of(p, actors)).toList();
	}

}
