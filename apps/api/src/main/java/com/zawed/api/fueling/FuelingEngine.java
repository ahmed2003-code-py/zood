package com.zawed.api.fueling;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.function.Supplier;

import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.fleet.TagGate;
import com.zawed.api.fleet.TagGate.TagAtPump;
import com.zawed.api.fueling.Fuelings.Fueling;
import com.zawed.api.fueling.Fuelings.Origin;
import com.zawed.api.fueling.Fuelings.Status;
import com.zawed.api.ledger.Ledger;
import com.zawed.api.ledger.Ledger.Account;
import com.zawed.api.ledger.Ledger.Entry;
import com.zawed.api.ledger.Ledger.Posted;
import com.zawed.api.ledger.Ledger.Poster;
import com.zawed.api.ledger.Ledger.Posting;
import com.zawed.api.ledger.Ledger.SourceType;
import com.zawed.api.ledger.Ledger.Wallet;
import com.zawed.api.network.PosPrincipal;
import com.zawed.api.network.StationDirectory;
import com.zawed.api.network.StationDirectory.StationRef;
import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.pricing.PriceBook;
import com.zawed.api.rules.FuelingRules;
import com.zawed.api.rules.FuelingRules.Decision;
import com.zawed.api.rules.FuelingRules.DeclineReason;
import com.zawed.api.rules.FuelingRules.Facts;
import com.zawed.api.rules.FuelingRules.Limit;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.Periods;
import com.zawed.api.rules.Periods.Period;
import com.zawed.api.tenancy.CompanyDirectory;
import com.zawed.api.tenancy.CompanyDirectory.CompanyRef;

/**
 * The online fueling (plan §3.4, B4 contract §3). One transaction per attempt:
 *
 * <ol>
 * <li>A {@code clientTxnId} seen before returns the stored outcome; nothing is decided twice.
 * <li>The tag's company wallet is locked, then the tag (always this order, ADR 0004). Every fueling of
 * a company is therefore serialised: limits and balance cannot be raced.
 * <li>The facts are read under those locks and {@link FuelingRules#decide} decides. The balance is
 * checked here, before anything is written, so the ledger posting that follows never refuses.
 * <li>A completed billable fueling posts wallet → station payable; then the fueling row (never
 * updated), the tag's cooldown and any hard alerts, all in the same transaction.
 * </ol>
 *
 * <p>A POS has no user. The engine runs in the platform scope, entered only here and only for a
 * device and agent the POS chain has just verified (B4 contract §1); what it writes is bounded by
 * those: the station is the device's, the company is the tag's.
 */
@Service
class FuelingEngine {

	/** A tanker truck with two tanks is under 1,500 L; anything larger is a typing error. */
	static final long MAX_VOLUME_ML = 1_500_000;

	enum WalletStatus {

		AVAILABLE, LOW, INSUFFICIENT, SUSPENDED

	}

	record Request(UUID clientTxnId, String tagUid, long volumeMl, FuelType fuelType, Instant deviceAt,
			String receiptLocale) {
	}

	/**
	 * @param walletStatus what the agent may be told about the wallet (never the amount); null on a
	 * replay, which does not read the wallet again
	 */
	record Result(Fueling fueling, boolean replayed, WalletStatus walletStatus, Instant cooldownUntil) {
	}

	private final Fuelings fuelings;
	private final Alerts alerts;
	private final TagGate tags;
	private final StationDirectory stations;
	private final CompanyDirectory companies;
	private final PriceBook prices;
	private final Ledger ledger;
	private final IdGenerator ids;
	private final TransactionTemplate tx;
	private final Clock clock;

	FuelingEngine(Fuelings fuelings, Alerts alerts, TagGate tags, StationDirectory stations,
			CompanyDirectory companies, PriceBook prices, Ledger ledger, IdGenerator ids, TransactionTemplate tx,
			Clock clock) {
		this.fuelings = fuelings;
		this.alerts = alerts;
		this.tags = tags;
		this.stations = stations;
		this.companies = companies;
		this.prices = prices;
		this.ledger = ledger;
		this.ids = ids;
		this.tx = tx;
		this.clock = clock;
	}

	Result fuel(PosPrincipal pos, Request request) {
		validate(pos, request);
		try {
			return asPlatform(() -> decide(pos, request));
		}
		catch (DuplicateKeyException ex) {
			// A concurrent request with the same clientTxnId committed first: this attempt rolled back
			// entirely (ledger posting included), and the stored outcome is the answer.
			return asPlatform(() -> replay(pos, request)).orElseThrow(() -> ex);
		}
	}

	private Result decide(PosPrincipal pos, Request request) {
		Optional<Result> seen = replay(pos, request);
		if (seen.isPresent()) {
			return seen.get();
		}
		TagGate.Bound bound = tags.find(request.tagUid())
			.orElseThrow(() -> new ApiException(HttpStatus.NOT_FOUND, "TAG_NOT_RECOGNISED",
					"This tag is not registered to any vehicle"));
		StationRef station = stations.findStation(pos.stationId())
			.filter(StationRef::active)
			.orElseThrow(() -> new ApiException(HttpStatus.CONFLICT, "STATION_NOT_ACTIVE",
					"The station is not open for fuelings"));

		// Locks: wallet first, then the tag.
		Wallet wallet = ledger.lockWallet(bound.companyId());
		seen = replay(pos, request);
		if (seen.isPresent()) {
			return seen.get();
		}
		TagAtPump tag = tags.lock(bound.tagId());
		CompanyRef company = companies.find(tag.companyId())
			.orElseThrow(() -> new IllegalStateException("Tag " + tag.tagId() + " has no company"));

		Instant now = clock.instant();
		FuelType fuel = request.fuelType() != null ? request.fuelType() : tag.vehicleFuel();
		Long unitPrice = station.fuelTypes().contains(fuel)
				? prices.priceAt(station.id(), fuel, now).map(PriceBook.Price::pricePiastresPerLitre).orElse(null)
				: null;
		List<Limit> limits = tag.vip() ? List.of() : limitsOf(tag, station, now);
		Decision decision = FuelingRules.decide(new Facts(now, new FuelingRules.Tag(tag.tagActive(), tag.cooldownUntil()),
				new FuelingRules.Vehicle(tag.vehicleActive(), tag.vehicleFuel(), tag.vip(), tag.tankCapacityMl()),
				new FuelingRules.Company(company.active()), request.fuelType(), unitPrice, limits,
				wallet.balancePiastres(), request.volumeMl()));
		// Read before this fueling is written: the VIP ceiling alert fires on the crossing only.
		long vipMonthBefore = tag.vip() && tag.vipMonthlyCeilingMl() != null
				? fuelings.volumeSince(tag.vehicleId(), Periods.start(now, Period.MONTH, station.zone())) : 0;

		UUID id = ids.next();
		String reference = fuelings.nextReference();
		long balanceAfter = wallet.balancePiastres();
		UUID ledgerTxn = null;
		if (decision.approved() && decision.billable() && decision.amountPiastres() > 0) {
			Posted posted = ledger.post(new Posting(SourceType.FUELING, id, reference, "Fueling " + reference,
					Poster.SYSTEM, List.of(Entry.debit(Account.companyWallet(company.id()), decision.amountPiastres()),
							Entry.credit(Account.stationPayable(station.id()), decision.amountPiastres()))));
			ledgerTxn = posted.transactionId();
			balanceAfter = posted.wallets().getFirst().balanceAfter();
		}
		Fueling fueling = new Fueling(id, request.clientTxnId(), reference,
				decision.approved() ? Status.COMPLETED : Status.DECLINED, decision.declineReason().orElse(null),
				Origin.ONLINE, company.id(), tag.vehicleId(), tag.tagId(), station.operatorId(), station.id(),
				pos.deviceId(), pos.agentId(), fuel, request.volumeMl(), decision.unitPricePiastres(),
				decision.amountPiastres(), decision.billable(), now, request.deviceAt(), now, request.receiptLocale(),
				tag.plate(), company.nameAr(), company.nameEn(), station.nameAr(), station.nameEn(), pos.agentWorkerId(),
				ledgerTxn);
		fuelings.insert(fueling);

		Instant cooldownUntil = tag.cooldownUntil();
		if (decision.approved()) {
			cooldownUntil = now.plus(Duration.ofMinutes(company.cooldownMinutes()));
			tags.startCooldown(tag.tagId(), cooldownUntil);
			raiseAlerts(fueling, tag, decision, vipMonthBefore);
		}
		return new Result(fueling, false, walletStatus(company, decision, balanceAfter), cooldownUntil);
	}

	private List<Limit> limitsOf(TagAtPump tag, StationRef station, Instant now) {
		List<Limit> limits = new ArrayList<>();
		for (TagGate.LimitRule rule : tags.activeLimits(tag.vehicleId())) {
			Instant since = Periods.start(now, rule.period(), station.zone());
			long used = rule.kind() == LimitKind.VOLUME ? fuelings.volumeSince(tag.vehicleId(), since)
					: fuelings.amountSince(tag.vehicleId(), since);
			limits.add(new Limit(rule.kind(), rule.period(), rule.maxValue(), used));
		}
		return limits;
	}

	/** PRD §10: flags, never blocks. */
	private void raiseAlerts(Fueling fueling, TagAtPump tag, Decision decision, long vipMonthBefore) {
		Alerts.Subject subject = new Alerts.Subject(fueling.companyId(), fueling.operatorId(), fueling.stationId(),
				fueling.vehicleId(), fueling.id());
		if (decision.tankCapacityExceeded()) {
			alerts.raise(Alerts.Type.TANK_CAPACITY_EXCEEDED, Alerts.Severity.CRITICAL, subject,
					Map.of("reference", fueling.reference(), "plate", fueling.plate(), "volumeMl", fueling.volumeMl(),
							"tankCapacityMl", tag.tankCapacityMl()),
					true);
		}
		Long ceiling = tag.vipMonthlyCeilingMl();
		if (tag.vip() && ceiling != null && vipMonthBefore <= ceiling && vipMonthBefore + fueling.volumeMl() > ceiling) {
			alerts.raise(Alerts.Type.VIP_CEILING_PASSED, Alerts.Severity.WARNING, subject,
					Map.of("reference", fueling.reference(), "plate", fueling.plate(), "monthVolumeMl",
							vipMonthBefore + fueling.volumeMl(), "ceilingMl", ceiling),
					false);
		}
	}

	private static WalletStatus walletStatus(CompanyRef company, Decision decision, long balanceAfter) {
		if (!company.active()) {
			return WalletStatus.SUSPENDED;
		}
		if (decision.declineReason().filter((r) -> r == DeclineReason.INSUFFICIENT_BALANCE).isPresent()) {
			return WalletStatus.INSUFFICIENT;
		}
		return balanceAfter < company.lowBalanceThresholdPiastres() ? WalletStatus.LOW : WalletStatus.AVAILABLE;
	}

	/**
	 * The stored outcome of a {@code clientTxnId}, when this device sent it before with the same
	 * fueling. The same id from another device, or for another volume or tag, is a client error.
	 */
	private Optional<Result> replay(PosPrincipal pos, Request request) {
		return fuelings.byClientTxnId(request.clientTxnId()).map((stored) -> {
			if (!stored.deviceId().equals(pos.deviceId())) {
				throw new ApiException(HttpStatus.CONFLICT, "CLIENT_TXN_CONFLICT",
						"This transaction id was used by another device");
			}
			boolean sameTag = tags.find(request.tagUid()).map((b) -> b.tagId().equals(stored.tagId())).orElse(false);
			if (!sameTag || stored.volumeMl() != request.volumeMl()) {
				throw new ApiException(HttpStatus.CONFLICT, "IDEMPOTENCY_MISMATCH",
						"This transaction id was used for a different fueling");
			}
			return new Result(stored, true, null, null);
		});
	}

	private static void validate(PosPrincipal pos, Request request) {
		if (!pos.hasAgent()) {
			throw new ApiException(HttpStatus.UNAUTHORIZED, "AGENT_SESSION_REQUIRED", "An agent must sign in");
		}
		if (request.volumeMl() <= 0 || request.volumeMl() > MAX_VOLUME_ML) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_VOLUME",
					"The volume must be between 1 ml and " + MAX_VOLUME_ML + " ml", "volumeMl", null);
		}
		if (!"ar".equals(request.receiptLocale()) && !"en".equals(request.receiptLocale())) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", "The receipt language is ar or en",
					"receiptLocale", null);
		}
	}

	private <T> T asPlatform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> tx.execute((status) -> work.get()));
	}

}
