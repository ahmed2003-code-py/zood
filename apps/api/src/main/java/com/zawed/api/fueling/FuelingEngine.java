package com.zawed.api.fueling;

import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;
import java.util.function.Supplier;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.dao.PessimisticLockingFailureException;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.fleet.TagGate;
import com.zawed.api.fleet.TagGate.TagAtPump;
import com.zawed.api.fueling.Fuelings.Conflict;
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
import com.zawed.api.network.AgentShifts;
import com.zawed.api.network.AgentShifts.AgentOnDevice;
import com.zawed.api.network.PosPrincipal;
import com.zawed.api.network.StationDirectory;
import com.zawed.api.network.StationDirectory.StationRef;
import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.money.MoneyMath;
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
 * The fueling engine (plan §3.4). One transaction per fueling, online or offline:
 *
 * <ol>
 * <li>A {@code clientTxnId} seen before returns the stored outcome; nothing is decided twice.
 * <li>The tag's company wallet is locked, then the vehicle (shared) and the tag (always this order,
 * ADR 0004, ADR 0018). Every fueling of a company is therefore serialised: limits and balance cannot
 * be raced.
 * <li>The facts are read under those locks and {@link FuelingRules} judges them.
 * <li>The ledger posting, the fueling row (never updated), the tag's cooldown and any hard alerts
 * are written in the same transaction.
 * </ol>
 *
 * <p><b>Online</b> ({@link #fuel}, B4 contract §3): the fueling is decided now. The balance is
 * checked before anything is written, so the posting that follows never refuses; a failing check
 * declines the fueling and moves no money.
 *
 * <p><b>Offline</b> ({@link #recordOffline}, B5 contract §4): the fueling already happened at the
 * pump, so it is recorded whatever the checks say. They are judged at the device's time, every one
 * that fails becomes a conflict and an alert, and what the wallet cannot cover becomes a debt on the
 * company's receivable account, so the station is credited in full and the wallet never goes below
 * zero.
 *
 * <p>A POS has no user. The engine runs in the platform scope, entered only here and only for a
 * device (and, online, an agent) the POS chain has just verified (B4 contract §1); what it writes is
 * bounded by those: the station is the device's, the company is the tag's.
 */
@Service
class FuelingEngine {

	/** A tanker truck with two tanks is under 1,500 L; anything larger is a typing error. */
	static final long MAX_VOLUME_ML = 1_500_000;

	/** B5 contract §3: a capture dated further ahead of server time than this is refused. */
	static final Duration MAX_CLOCK_AHEAD = Duration.ofMinutes(5);

	/** B5 contract §3: a capture older than this is still recorded, and flagged. */
	static final Duration STALE_AFTER = Duration.ofDays(7);

	/**
	 * Conflicts that mean fuel went to something that was blocked, or money that could not be
	 * collected the normal way; the others (a cooldown, a limit, a late capture) are warnings.
	 */
	private static final Set<Conflict> CRITICAL_CONFLICTS = EnumSet.of(Conflict.TAG_INACTIVE, Conflict.VEHICLE_INACTIVE,
			Conflict.COMPANY_FROZEN, Conflict.FUEL_NOT_AVAILABLE, Conflict.INSUFFICIENT_BALANCE, Conflict.AGENT_UNVERIFIED,
			Conflict.DECLINED_ONLINE, Conflict.STATION_NOT_ACTIVE, Conflict.OVER_OFFLINE_ALLOWANCE);

	/** Conflicts whose payable waits for Operations before a station payout (B6). */
	private static final Set<Conflict> HOLD_PAYABLE = EnumSet.of(Conflict.STATION_NOT_ACTIVE,
			Conflict.OVER_OFFLINE_ALLOWANCE);

	/** How far back one device's offline spending for one company is summed against the bound. */
	static final Duration EXPOSURE_WINDOW = Duration.ofHours(24);

	enum WalletStatus {

		AVAILABLE, LOW, INSUFFICIENT, SUSPENDED

	}

	record Request(UUID clientTxnId, String tagUid, long volumeMl, FuelType fuelType, Instant deviceAt,
			String receiptLocale) {
	}

	/**
	 * A fueling captured while the device was offline (B5 contract §3).
	 *
	 * @param deviceAt when it happened, by the device's clock: the instant the checks are judged at
	 * @param agentId who captured it; trusted only if the server's own session history agrees
	 */
	record OfflineRequest(UUID clientTxnId, String tagUid, long volumeMl, FuelType fuelType, Instant deviceAt,
			UUID agentId, String receiptLocale, Long devicePricePiastres) {

		/** The same capture under another id (recorded after its online attempt was declined). */
		OfflineRequest withClientTxnId(UUID id) {
			return new OfflineRequest(id, tagUid, volumeMl, fuelType, deviceAt, agentId, receiptLocale, devicePricePiastres);
		}

	}

	/**
	 * @param walletStatus what the agent may be told about the wallet (never the amount); null on a
	 * replay, which does not read the wallet again, and for an offline fueling
	 */
	record Result(Fueling fueling, boolean replayed, WalletStatus walletStatus, Instant cooldownUntil) {
	}

	/** The rows a fueling is judged on, read while their locks are held. */
	private record Locked(Wallet wallet, TagAtPump tag, CompanyRef company, boolean vip) {
	}

	private final Fuelings fuelings;
	private final Alerts alerts;
	private final TagGate tags;
	private final StationDirectory stations;
	private final AgentShifts shifts;
	private final CompanyDirectory companies;
	private final PriceBook prices;
	private final Ledger ledger;
	private final IdGenerator ids;
	private final TransactionTemplate tx;
	private final Clock clock;
	private final boolean vipEnabled;
	private final long exposureCap;

	/**
	 * @param vipEnabled the VIP flow (PRD 4.4: not billed, no limits) is modelled but switched off
	 * until the owner decides who pays for VIP fuel (plan §1, open question 4). While off, a VIP
	 * vehicle fuels exactly like a normal one: limits, balance, billed.
	 */
	FuelingEngine(Fuelings fuelings, Alerts alerts, TagGate tags, StationDirectory stations, AgentShifts shifts,
			CompanyDirectory companies, PriceBook prices, Ledger ledger, IdGenerator ids, TransactionTemplate tx,
			Clock clock, @Value("${zawed.fueling.vip-enabled:false}") boolean vipEnabled,
			@Value("${zawed.sync.offline-exposure-cap-piastres:1000000}") long exposureCap) {
		this.fuelings = fuelings;
		this.alerts = alerts;
		this.tags = tags;
		this.stations = stations;
		this.shifts = shifts;
		this.companies = companies;
		this.prices = prices;
		this.ledger = ledger;
		this.ids = ids;
		this.tx = tx;
		this.clock = clock;
		this.vipEnabled = vipEnabled;
		this.exposureCap = exposureCap;
	}

	// -------------------------------------------------------------------------------------- online

	Result fuel(PosPrincipal pos, Request request) {
		validate(pos, request);
		return once(pos, request.clientTxnId(), request.tagUid(), request.volumeMl(), () -> decide(pos, request));
	}

	private Result decide(PosPrincipal pos, Request request) {
		Optional<Result> seen = replay(pos, request.clientTxnId(), request.tagUid(), request.volumeMl());
		if (seen.isPresent()) {
			return seen.get();
		}
		TagGate.Bound bound = bound(request.tagUid());
		StationRef station = stations.findStation(pos.stationId())
			.filter(StationRef::active)
			.orElseThrow(() -> new ApiException(HttpStatus.CONFLICT, "STATION_NOT_ACTIVE",
					"The station is not open for fuelings"));

		Wallet wallet = ledger.lockWallet(bound.companyId());
		seen = replay(pos, request.clientTxnId(), request.tagUid(), request.volumeMl());
		if (seen.isPresent()) {
			return seen.get();
		}
		Locked locked = lockTag(wallet, bound);
		TagAtPump tag = locked.tag();
		CompanyRef company = locked.company();

		Instant now = clock.instant();
		Facts facts = facts(locked, station, request.fuelType(), request.volumeMl(), now, tag.cooldownUntil());
		Decision decision = FuelingRules.decide(facts);
		// Read before this fueling is written: the VIP ceiling alert fires on the crossing only.
		long vipMonthBefore = vipMonthBefore(locked, station, now);

		UUID id = ids.next();
		String reference = fuelings.nextReference();
		long balanceAfter = wallet.balancePiastres();
		UUID ledgerTxn = null;
		if (decision.approved() && decision.billable() && decision.amountPiastres() > 0) {
			Posted posted = post(id, reference, List.of(Entry.debit(Account.companyWallet(company.id()), decision.amountPiastres()),
					Entry.credit(Account.stationPayable(station.id()), decision.amountPiastres())));
			ledgerTxn = posted.transactionId();
			balanceAfter = posted.wallets().getFirst().balanceAfter();
		}
		Fueling fueling = new Fueling(id, request.clientTxnId(), reference,
				decision.approved() ? Status.COMPLETED : Status.DECLINED, decision.declineReason().orElse(null), List.of(),
				Origin.ONLINE, company.id(), tag.vehicleId(), tag.tagId(), station.operatorId(), station.id(),
				pos.deviceId(), pos.agentId(), fuelOf(tag, request.fuelType()), request.volumeMl(),
				decision.unitPricePiastres(), decision.amountPiastres(), decision.billable(), now, request.deviceAt(), now,
				request.receiptLocale(), tag.plate(), company.nameAr(), company.nameEn(), station.nameAr(),
				station.nameEn(), pos.agentWorkerId(), ledgerTxn, false);
		fuelings.insert(fueling);

		Instant cooldownUntil = tag.cooldownUntil();
		if (decision.approved()) {
			cooldownUntil = now.plus(Duration.ofMinutes(company.cooldownMinutes()));
			tags.startCooldown(tag.tagId(), cooldownUntil);
			raiseAlerts(fueling, tag, locked.vip(), decision, vipMonthBefore);
		}
		return new Result(fueling, false, walletStatus(company, decision, balanceAfter), cooldownUntil);
	}

	// ------------------------------------------------------------------------------------- offline

	/**
	 * Records a fueling that was captured offline (B5 contract §4). It is never declined and never
	 * reversed (PRD §7.5): every check that fails is kept on the fueling as a conflict and raises one
	 * alert. The caller gets the stored fueling back, {@code replayed} when this device sent the
	 * same capture before.
	 *
	 * @param device the device that sends its queue, from its token. Whoever is signed in on it now
	 * plays no part: the capture's agent is {@link OfflineRequest#agentId}, checked against the
	 * session history
	 * @throws ApiException when nothing can be recorded: {@code INVALID_VOLUME},
	 * {@code DEVICE_CLOCK_SKEW}, {@code TAG_NOT_RECOGNISED}, {@code AGENT_NOT_ON_DEVICE},
	 * {@code CLIENT_TXN_CONFLICT}, {@code IDEMPOTENCY_MISMATCH}, {@code DECLINED_ONLINE}
	 */
	Result recordOffline(PosPrincipal device, OfflineRequest request) {
		validate(request);
		Result result = once(device, request.clientTxnId(), request.tagUid(), request.volumeMl(),
				() -> record(device, request, false));
		if (result.replayed() && !result.fueling().completed()) {
			// The terminal sent this fueling online, the server declined it, the answer never arrived,
			// and the terminal then let the fuel go on its offline rules under the same id. The id's
			// stored outcome is the decline and stays so (a repeated id changes nothing), but the fuel
			// left the pump: it is recorded as an offline fueling of its own, under an id derived from
			// the first so that sending it again finds it, flagged DECLINED_ONLINE (B5 review 1).
			OfflineRequest after = request.withClientTxnId(afterDecline(request.clientTxnId()));
			Result recorded = once(device, after.clientTxnId(), after.tagUid(), after.volumeMl(),
					() -> record(device, after, true));
			return recorded;
		}
		return result;
	}

	/** The id an offline capture is recorded under after its online attempt was declined. */
	static UUID afterDecline(UUID clientTxnId) {
		return UUID.nameUUIDFromBytes(("offline-after-decline:" + clientTxnId).getBytes(StandardCharsets.UTF_8));
	}

	private Result record(PosPrincipal device, OfflineRequest request, boolean declinedOnline) {
		Optional<Result> seen = replay(device, request.clientTxnId(), request.tagUid(), request.volumeMl());
		if (seen.isPresent()) {
			return seen.get();
		}
		TagGate.Bound bound = bound(request.tagUid());
		Instant now = clock.instant();
		List<Conflict> conflicts = new ArrayList<>();
		// A capture dated in the future is judged at the time it arrived (B5 review 1): it cannot have
		// happened later than that, and a future date would push prices, periods and the cooldown ahead.
		Instant at = request.deviceAt();
		if (at.isAfter(now.plus(MAX_CLOCK_AHEAD))) {
			at = now;
			conflicts.add(Conflict.DEVICE_CLOCK_SKEW);
		}
		// The agent must be of this station; whose session cannot be confirmed for the time is recorded
		// and flagged, because the fuel left the pump either way (B5 review 1).
		Optional<AgentOnDevice> onDevice = shifts.onDeviceAt(device.deviceId(), device.stationId(), request.agentId(), at);
		AgentOnDevice agent = onDevice.or(() -> shifts.ofStation(device.stationId(), request.agentId()))
			.orElseThrow(() -> new ApiException(HttpStatus.FORBIDDEN, "AGENT_NOT_OF_STATION",
					"This agent does not work at this device's station"));
		if (onDevice.isEmpty()) {
			conflicts.add(Conflict.AGENT_UNVERIFIED);
		}
		if (declinedOnline) {
			conflicts.add(Conflict.DECLINED_ONLINE);
		}
		// Not required to be open, unlike online: the fuel was dispensed and is recorded (PRD §7.5),
		// but a station that was not open is flagged and its payable held (B5 review 7).
		StationRef station = stations.findStation(device.stationId())
			.orElseThrow(() -> new IllegalStateException("Device " + device.deviceId() + " has no station"));
		if (!station.active()) {
			conflicts.add(Conflict.STATION_NOT_ACTIVE);
		}

		Wallet wallet = ledger.lockWallet(bound.companyId());
		seen = replay(device, request.clientTxnId(), request.tagUid(), request.volumeMl());
		if (seen.isPresent()) {
			return seen.get();
		}
		Locked locked = lockTag(wallet, bound);
		TagAtPump tag = locked.tag();
		CompanyRef company = locked.company();

		// The same checks as online, judged at the device's time: the price in force then, the
		// limits of the period that contains it, and the cooldown as the fuelings around it give it.
		Duration cooldown = Duration.ofMinutes(company.cooldownMinutes());
		Facts facts = facts(locked, station, request.fuelType(), request.volumeMl(), at,
				cooldownAround(tag, cooldown, at));
		Decision decision = FuelingRules.decide(facts);
		FuelingRules.allFailures(facts).forEach((reason) -> conflicts.add(Conflict.of(reason)));
		if (at.isBefore(now.minus(STALE_AFTER))) {
			conflicts.add(Conflict.STALE_CAPTURE);
		}
		long vipMonthBefore = vipMonthBefore(locked, station, at);

		// The price. With none in force at the capture time (the fuel left the station's list after
		// the snapshot, or the device's clock is before the first price), the one in force now is
		// charged, so a dispensed fueling is never worth nothing (B5 review 9).
		long unitPrice = decision.unitPricePiastres();
		long amount = decision.amountPiastres();
		if (unitPrice == 0) {
			FuelType fuel = fuelOf(tag, request.fuelType());
			Optional<Long> fallback = prices.priceAt(station.id(), fuel, at)
				.or(() -> prices.priceAt(station.id(), fuel, now))
				.map(PriceBook.Price::pricePiastresPerLitre);
			if (fallback.isPresent()) {
				unitPrice = fallback.get();
				amount = MoneyMath.amountForVolume(request.volumeMl(), unitPrice);
			}
		}
		if (request.devicePricePiastres() != null && request.devicePricePiastres() != unitPrice) {
			conflicts.add(Conflict.PRICE_MISMATCH);
		}
		// The bound on what one device may spend offline for one company (B5 review 2): the terminal
		// keeps to its allowance, but only an honest terminal does. Past the bound the fueling is still
		// recorded, flagged, its payable held for Operations, and a critical alert raised.
		boolean overExposure = decision.billable() && amount > 0
				&& fuelings.offlineAmountSince(device.deviceId(), company.id(), at.minus(EXPOSURE_WINDOW)) + amount > exposureCap;
		if (overExposure) {
			conflicts.add(Conflict.OVER_OFFLINE_ALLOWANCE);
		}
		boolean held = conflicts.stream().anyMatch(HOLD_PAYABLE::contains);

		// The money (B5 contract §4). The station dispensed the fuel and is credited in full. The
		// wallet pays what it holds, read under its lock, so it stops at exactly zero; the rest is
		// the company's debt on its receivable account. One balanced posting for the fueling.
		UUID id = ids.next();
		String reference = fuelings.nextReference();
		long owed = 0;
		UUID ledgerTxn = null;
		if (decision.billable() && amount > 0) {
			long fromWallet = Math.min(wallet.balancePiastres(), amount);
			owed = amount - fromWallet;
			List<Entry> entries = new ArrayList<>();
			if (fromWallet > 0) {
				entries.add(Entry.debit(Account.companyWallet(company.id()), fromWallet));
			}
			if (owed > 0) {
				entries.add(Entry.debit(Account.companyReceivable(company.id()), owed));
			}
			entries.add(Entry.credit(Account.stationPayable(station.id()), amount));
			ledgerTxn = post(id, reference, entries).transactionId();
		}
		Fueling fueling = new Fueling(id, request.clientTxnId(), reference, Status.COMPLETED, null, conflicts,
				Origin.OFFLINE, company.id(), tag.vehicleId(), tag.tagId(), station.operatorId(), station.id(),
				device.deviceId(), agent.agentId(), fuelOf(tag, request.fuelType()), request.volumeMl(), unitPrice, amount,
				decision.billable(), at, request.deviceAt(), now, request.receiptLocale(), tag.plate(), company.nameAr(),
				company.nameEn(), station.nameAr(), station.nameEn(), agent.workerId(), ledgerTxn, held);
		fuelings.insert(fueling);

		// The mirror of the tag's cooldown only ever moves forward: a capture older than the tag's
		// last fueling must not shorten the cooldown that fueling started.
		Instant cooldownUntil = at.plus(cooldown);
		if (tag.cooldownUntil() == null || cooldownUntil.isAfter(tag.cooldownUntil())) {
			tags.startCooldown(tag.tagId(), cooldownUntil);
		}
		else {
			cooldownUntil = tag.cooldownUntil();
		}
		raiseAlerts(fueling, tag, locked.vip(), decision, vipMonthBefore);
		if (!conflicts.isEmpty()) {
			raiseConflict(fueling, owed);
		}
		if (overExposure) {
			alerts.raise(Alerts.Type.OFFLINE_EXPOSURE, Alerts.Severity.CRITICAL,
					new Alerts.Subject(fueling.companyId(), fueling.operatorId(), fueling.stationId(), fueling.deviceId(),
							fueling.vehicleId(), fueling.id()),
					Map.of("reference", fueling.reference(), "deviceCode", device.deviceCode(), "capPiastres", exposureCap,
							"amountPiastres", amount),
					false);
		}
		return new Result(fueling, false, null, cooldownUntil);
	}

	/**
	 * The cooldown an offline capture at {@code at} collides with, or null. The tag's own
	 * {@code cooldown_until} is the mirror of its <em>latest</em> fueling, which may be one that
	 * happened after this capture and far enough from it; judging the capture by it would flag a
	 * conflict that never was. So the fuelings themselves are asked: two fuelings of one vehicle
	 * closer than the cooldown are a conflict, whichever of them reached the server last. (The
	 * cooldown follows the vehicle: a replacement tag inherits it.)
	 */
	private Instant cooldownAround(TagAtPump tag, Duration cooldown, Instant at) {
		return fuelings.latestCompletedBetween(tag.vehicleId(), at.minus(cooldown), at.plus(cooldown))
			.map((other) -> other.plus(cooldown))
			.orElse(null);
	}

	/** One alert per offline fueling with conflicts, for Operations and the company (B5 contract §4). */
	private void raiseConflict(Fueling fueling, long owedPiastres) {
		boolean critical = fueling.conflicts().stream().anyMatch(CRITICAL_CONFLICTS::contains);
		Map<String, Object> details = new LinkedHashMap<>();
		details.put("reference", fueling.reference());
		details.put("plate", fueling.plate());
		details.put("conflicts", fueling.conflicts().stream().map(Conflict::name).toList());
		details.put("deviceAt", fueling.deviceAt().toString());
		details.put("volumeMl", fueling.volumeMl());
		details.put("amountPiastres", fueling.amountPiastres());
		if (owedPiastres > 0) {
			// The part of the amount the wallet could not cover: the company's own debt.
			details.put("owedPiastres", owedPiastres);
		}
		alerts.raise(Alerts.Type.SYNC_CONFLICT, critical ? Alerts.Severity.CRITICAL : Alerts.Severity.WARNING,
				new Alerts.Subject(fueling.companyId(), fueling.operatorId(), fueling.stationId(), fueling.deviceId(),
						fueling.vehicleId(), fueling.id()),
				details, true);
	}

	// ------------------------------------------------------------------------- shared by both paths

	/**
	 * Runs one attempt in its own transaction and answers a concurrent duplicate from the stored
	 * row. The attempt is tried once more if PostgreSQL chose it as a deadlock victim: the lock order
	 * makes that unlikely (ADR 0018), and the retry is safe because the attempt rolled back entirely.
	 */
	private Result once(PosPrincipal pos, UUID clientTxnId, String tagUid, long volumeMl, Supplier<Result> attempt) {
		try {
			try {
				return asPlatform(attempt);
			}
			catch (PessimisticLockingFailureException ex) {
				return asPlatform(attempt);
			}
		}
		catch (DuplicateKeyException ex) {
			// A concurrent request with the same clientTxnId committed first: this attempt rolled back
			// entirely (ledger posting included), and the stored outcome is the answer.
			return asPlatform(() -> replay(pos, clientTxnId, tagUid, volumeMl)).orElseThrow(() -> ex);
		}
	}

	private TagGate.Bound bound(String tagUid) {
		return tags.find(tagUid)
			.orElseThrow(() -> new ApiException(HttpStatus.NOT_FOUND, "TAG_NOT_RECOGNISED",
					"This tag is not registered to any vehicle"));
	}

	/** The second and third locks, after the wallet: the vehicle (shared) and the tag. */
	private Locked lockTag(Wallet wallet, TagGate.Bound bound) {
		TagAtPump tag = tags.lock(bound.tagId());
		if (!tag.companyId().equals(bound.companyId())) {
			// A tag never changes company; if it did, the wallet locked above would be the wrong one.
			throw new IllegalStateException("Tag " + tag.tagId() + " changed company during a fueling");
		}
		CompanyRef company = companies.find(tag.companyId())
			.orElseThrow(() -> new IllegalStateException("Tag " + tag.tagId() + " has no company"));
		return new Locked(wallet, tag, company, vipEnabled && tag.vip());
	}

	/**
	 * What the rules judge, read under the locks, as of {@code at}: now for an online fueling, the
	 * device's time for an offline one.
	 *
	 * @param cooldownUntil the cooldown that applies at {@code at}
	 */
	private Facts facts(Locked locked, StationRef station, FuelType requestedFuel, long volumeMl, Instant at,
			Instant cooldownUntil) {
		TagAtPump tag = locked.tag();
		FuelType fuel = fuelOf(tag, requestedFuel);
		Long unitPrice = station.fuelTypes().contains(fuel)
				? prices.priceAt(station.id(), fuel, at).map(PriceBook.Price::pricePiastresPerLitre).orElse(null) : null;
		List<Limit> limits = locked.vip() ? List.of() : limitsOf(tag, station, at);
		return new Facts(at, new FuelingRules.Tag(tag.tagActive(), cooldownUntil),
				new FuelingRules.Vehicle(tag.vehicleActive(), tag.vehicleFuel(), locked.vip(), tag.tankCapacityMl()),
				new FuelingRules.Company(locked.company().active()), requestedFuel, unitPrice, limits,
				locked.wallet().balancePiastres(), volumeMl);
	}

	private static FuelType fuelOf(TagAtPump tag, FuelType requestedFuel) {
		return requestedFuel != null ? requestedFuel : tag.vehicleFuel();
	}

	/**
	 * Each active limit with what the vehicle used in the period that contains {@code at}, in the
	 * station's zone. The whole period counts, also the part after {@code at}: when an offline
	 * capture arrives late, a limit it breaks together with later fuelings is still found.
	 */
	private List<Limit> limitsOf(TagAtPump tag, StationRef station, Instant at) {
		List<Limit> limits = new ArrayList<>();
		for (TagGate.LimitRule rule : tags.activeLimits(tag.vehicleId())) {
			Instant from = Periods.start(at, rule.period(), station.zone());
			Instant to = Periods.end(at, rule.period(), station.zone());
			long used = rule.kind() == LimitKind.VOLUME ? fuelings.volumeIn(tag.vehicleId(), from, to)
					: fuelings.amountIn(tag.vehicleId(), from, to);
			limits.add(new Limit(rule.kind(), rule.period(), rule.maxValue(), used));
		}
		return limits;
	}

	/** What a VIP vehicle used in the month of {@code at} before this fueling; 0 when no ceiling applies. */
	private long vipMonthBefore(Locked locked, StationRef station, Instant at) {
		if (!locked.vip() || locked.tag().vipMonthlyCeilingMl() == null) {
			return 0;
		}
		return fuelings.volumeIn(locked.tag().vehicleId(), Periods.start(at, Period.MONTH, station.zone()),
				Periods.end(at, Period.MONTH, station.zone()));
	}

	/** One posting per fueling: its source is the fueling id, so it can never post twice. */
	private Posted post(UUID fuelingId, String reference, List<Entry> entries) {
		try {
			return ledger.post(new Posting(SourceType.FUELING, fuelingId, reference, "Fueling " + reference, Poster.SYSTEM,
					entries));
		}
		catch (ApiException ex) {
			// Unreachable while the amounts are decided under the wallet lock. If it ever happens, it
			// is a bug, and the ledger's refusal (which names the balance) must not reach the pump.
			throw new IllegalStateException("The ledger refused a decided fueling: " + ex.code(), ex);
		}
	}

	/** PRD §10: flags, never blocks. */
	private void raiseAlerts(Fueling fueling, TagAtPump tag, boolean vip, Decision decision, long vipMonthBefore) {
		Alerts.Subject subject = new Alerts.Subject(fueling.companyId(), fueling.operatorId(), fueling.stationId(),
				fueling.deviceId(), fueling.vehicleId(), fueling.id());
		if (decision.tankCapacityExceeded()) {
			alerts.raise(Alerts.Type.TANK_CAPACITY_EXCEEDED, Alerts.Severity.CRITICAL, subject,
					Map.of("reference", fueling.reference(), "plate", fueling.plate(), "volumeMl", fueling.volumeMl(),
							"tankCapacityMl", tag.tankCapacityMl()),
					true);
		}
		Long ceiling = tag.vipMonthlyCeilingMl();
		if (vip && ceiling != null && vipMonthBefore <= ceiling && vipMonthBefore + fueling.volumeMl() > ceiling) {
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
	private Optional<Result> replay(PosPrincipal pos, UUID clientTxnId, String tagUid, long volumeMl) {
		return fuelings.byClientTxnId(clientTxnId).map((stored) -> {
			if (!stored.deviceId().equals(pos.deviceId())) {
				throw new ApiException(HttpStatus.CONFLICT, "CLIENT_TXN_CONFLICT",
						"This transaction id was used by another device");
			}
			boolean sameTag = tags.find(tagUid).map((b) -> b.tagId().equals(stored.tagId())).orElse(false);
			if (!sameTag || stored.volumeMl() != volumeMl) {
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
		validate(request.volumeMl(), request.receiptLocale());
	}

	private void validate(OfflineRequest request) {
		validate(request.volumeMl(), request.receiptLocale());
	}

	private static void validate(long volumeMl, String receiptLocale) {
		if (volumeMl <= 0 || volumeMl > MAX_VOLUME_ML) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_VOLUME",
					"The volume must be between 1 ml and " + MAX_VOLUME_ML + " ml", "volumeMl", null);
		}
		if (!"ar".equals(receiptLocale) && !"en".equals(receiptLocale)) {
			throw new ApiException(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", "The receipt language is ar or en",
					"receiptLocale", null);
		}
	}

	private <T> T asPlatform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> tx.execute((status) -> work.get()));
	}

}
