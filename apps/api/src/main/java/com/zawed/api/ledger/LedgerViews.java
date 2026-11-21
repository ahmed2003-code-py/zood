package com.zawed.api.ledger;

import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.stream.Stream;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Component;

import com.zawed.api.identity.ActorDirectory;
import com.zawed.api.identity.Actors;
import com.zawed.api.ledger.LedgerApi.TopUpResponse;
import com.zawed.api.ledger.LedgerApi.WalletAdjustmentResponse;
import com.zawed.api.ledger.TopUps.TopUp;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.Page;

/**
 * Top-ups and wallet adjustments as the signed-in caller reads them: who acted, by name where the
 * caller may see it ({@link ActorDirectory}), and the receiving account by its label (QA round 1).
 */
@Component
class LedgerViews {

	private final ActorDirectory people;
	private final Map<String, String> accountLabels;

	/**
	 * {@code zawed.finance.receiving-account-labels} is a list of {@code CODE=Label} entries, for example
	 * {@code MAIN=Banque Misr ••4821}. A label is shown to Operations and to companies, so it names the
	 * bank and at most the last digits of the account. An entry without {@code =} stops the boot.
	 */
	LedgerViews(ActorDirectory people, @Value("${zawed.finance.receiving-account-labels:}") List<String> labels) {
		this.people = people;
		Map<String, String> parsed = new HashMap<>();
		for (String entry : labels) {
			if (entry == null || entry.isBlank()) {
				continue;
			}
			int split = entry.indexOf('=');
			if (split <= 0 || entry.substring(split + 1).isBlank()) {
				throw new IllegalStateException("zawed.finance.receiving-account-labels: expected CODE=Label");
			}
			parsed.put(entry.substring(0, split).trim().toUpperCase(Locale.ROOT), entry.substring(split + 1).trim());
		}
		this.accountLabels = Map.copyOf(parsed);
	}

	TopUpResponse topUp(ZawedPrincipal viewer, TopUp topUp) {
		return topUps(viewer, List.of(topUp)).get(0);
	}

	Page<TopUpResponse> topUps(ZawedPrincipal viewer, Page<TopUp> page) {
		return new Page<>(topUps(viewer, page.items()), page.nextCursor());
	}

	private List<TopUpResponse> topUps(ZawedPrincipal viewer, List<TopUp> topUps) {
		Actors actors = people.describe(viewer, topUps.stream()
			.flatMap((t) -> Stream.of(t.requestedBy(), t.verifiedBy(), t.decidedBy()))
			.filter(Objects::nonNull)
			.toList());
		return topUps.stream()
			.map((t) -> TopUpResponse.of(t, actors, accountLabels.get(t.receivingAccount())))
			.toList();
	}

	WalletAdjustmentResponse adjustment(ZawedPrincipal viewer, WalletAdjustments.Adjustment adjustment) {
		return adjustments(viewer, List.of(adjustment)).get(0);
	}

	Page<WalletAdjustmentResponse> adjustments(ZawedPrincipal viewer, Page<WalletAdjustments.Adjustment> page) {
		return new Page<>(adjustments(viewer, page.items()), page.nextCursor());
	}

	private List<WalletAdjustmentResponse> adjustments(ZawedPrincipal viewer,
			List<WalletAdjustments.Adjustment> adjustments) {
		Actors actors = people.describe(viewer, adjustments.stream()
			.flatMap((a) -> Stream.of(a.requestedBy(), a.decidedBy()))
			.filter(Objects::nonNull)
			.toList());
		return adjustments.stream().map((a) -> WalletAdjustmentResponse.of(a, actors)).toList();
	}

}
