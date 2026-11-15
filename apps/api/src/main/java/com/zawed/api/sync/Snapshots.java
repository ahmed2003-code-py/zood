package com.zawed.api.sync;

import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneId;
import java.util.ArrayList;
import java.util.Base64;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.function.Supplier;

import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Service;
import org.springframework.transaction.support.TransactionTemplate;

import com.zawed.api.fleet.FleetSnapshot;
import com.zawed.api.fleet.FleetSnapshot.Fleet;
import com.zawed.api.fleet.FleetSnapshot.SnapshotLimit;
import com.zawed.api.fleet.FleetSnapshot.SnapshotVehicle;
import com.zawed.api.fueling.FuelingUsage;
import com.zawed.api.fueling.FuelingUsage.Usage;
import com.zawed.api.ledger.WalletBalances;
import com.zawed.api.network.PosPrincipal;
import com.zawed.api.network.StationDirectory;
import com.zawed.api.network.StationDirectory.StationRef;
import com.zawed.api.platform.FuelType;
import com.zawed.api.platform.crypto.LookupHasher;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.DbScope;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.pricing.PriceBook;
import com.zawed.api.rules.FuelingRules.LimitKind;
import com.zawed.api.rules.Periods;
import com.zawed.api.rules.Periods.Period;
import com.zawed.api.tenancy.CompanyDirectory;
import com.zawed.api.tenancy.CompanyDirectory.CompanyRef;

/**
 * The snapshot a POS caches to decide fuelings offline (B5 contract §2). What it leaves out is as
 * deliberate as what it carries: no tag UIDs (each tag is a digest under a key for this device
 * only), no wallet balances (an allowance capped by configuration), no PIN hashes.
 *
 * <p>A device has no tenant scope; the snapshot is read in the platform scope after the POS chain
 * has verified the device (an escalation listed in {@code ArchitectureTests}).
 */
@Service
class Snapshots {

	/** How far ahead scheduled price changes are included. */
	static final Duration PRICE_HORIZON = Duration.ofDays(7);
	static final String TAG_KEY_NAMESPACE = "pos-tag-digest";
	private static final int SNAPSHOTS_PER_MINUTE = 6;

	enum SnapshotWalletStatus {

		AVAILABLE, LOW, INSUFFICIENT, SUSPENDED

	}

	record PosSnapshot(
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant serverTime,
			@Schema(requiredMode = RequiredMode.REQUIRED) int contract,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean vipEnabled,
			@Schema(requiredMode = RequiredMode.REQUIRED) SnapshotStation station,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<SnapshotPrice> prices,
			@Schema(requiredMode = RequiredMode.REQUIRED) String tagKey,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<FleetSnapshot.SnapshotTag> tags,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<SnapshotVehicle> vehicles,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<SnapshotLimitUsage> limits,
			@Schema(requiredMode = RequiredMode.REQUIRED) List<SnapshotCompany> companies) {
	}

	record SnapshotStation(
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameAr,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameEn,
			@Schema(requiredMode = RequiredMode.REQUIRED) String timeZone,
			@Schema(requiredMode = RequiredMode.REQUIRED) Set<FuelType> fuelTypes,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean open) {
	}

	record SnapshotPrice(
			@Schema(requiredMode = RequiredMode.REQUIRED) FuelType fuelType,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean national,
			@Schema(requiredMode = RequiredMode.REQUIRED) long pricePiastresPerLitre,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant effectiveFrom) {
	}

	record SnapshotLimitUsage(
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID vehicleId,
			@Schema(requiredMode = RequiredMode.REQUIRED) LimitKind kind,
			@Schema(requiredMode = RequiredMode.REQUIRED) Period period,
			@Schema(requiredMode = RequiredMode.REQUIRED) long maxValue,
			@Schema(requiredMode = RequiredMode.REQUIRED) long used,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant periodStart) {
	}

	record SnapshotCompany(
			@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameAr,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameEn,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean active,
			@Schema(requiredMode = RequiredMode.REQUIRED) int cooldownMinutes,
			@Schema(requiredMode = RequiredMode.REQUIRED) SnapshotWalletStatus walletStatus,
			@Schema(requiredMode = RequiredMode.REQUIRED) long offlineAllowancePiastres) {
	}

	private final StationDirectory stations;
	private final FleetSnapshot fleet;
	private final FuelingUsage usage;
	private final CompanyDirectory companies;
	private final WalletBalances wallets;
	private final PriceBook prices;
	private final LookupHasher keys;
	private final RateLimiter limiter;
	private final TransactionTemplate tx;
	private final Clock clock;
	private final long allowanceCap;
	private final boolean vipEnabled;

	Snapshots(StationDirectory stations, FleetSnapshot fleet, FuelingUsage usage, CompanyDirectory companies,
			WalletBalances wallets, PriceBook prices, LookupHasher keys, RateLimiter limiter, TransactionTemplate tx,
			Clock clock, @Value("${zawed.sync.offline-allowance-cap-piastres:500000}") long allowanceCap,
			@Value("${zawed.fueling.vip-enabled:false}") boolean vipEnabled) {
		this.stations = stations;
		this.fleet = fleet;
		this.usage = usage;
		this.companies = companies;
		this.wallets = wallets;
		this.prices = prices;
		this.keys = keys;
		this.limiter = limiter;
		this.tx = tx;
		this.clock = clock;
		this.allowanceCap = allowanceCap;
		this.vipEnabled = vipEnabled;
	}

	PosSnapshot snapshotFor(PosPrincipal device) {
		limiter.acquire("pos-snapshot:" + device.deviceId(), SNAPSHOTS_PER_MINUTE, Duration.ofMinutes(1));
		Instant now = clock.instant();
		byte[] tagKey = tagKey(device.deviceId());
		return asPlatform(() -> {
			StationRef station = stations.findStation(device.stationId())
				.orElseThrow(() -> new ApiException(HttpStatus.CONFLICT, "STATION_NOT_ACTIVE", "The station is not open"));
			ZoneId zone = station.zone();

			List<SnapshotPrice> schedule = new ArrayList<>();
			for (FuelType fuel : FuelType.values()) {
				if (station.fuelTypes().contains(fuel)) {
					prices.entriesFor(station.id(), fuel, now, now.plus(PRICE_HORIZON))
						.forEach((p) -> schedule.add(new SnapshotPrice(p.fuelType(), p.stationId() == null,
								p.pricePiastresPerLitre(), p.effectiveFrom())));
				}
			}

			Fleet snapshot = fleet.snapshot(tagKey);
			Instant day = Periods.start(now, Period.DAY, zone);
			Instant week = Periods.start(now, Period.WEEK, zone);
			Instant month = Periods.start(now, Period.MONTH, zone);
			Map<UUID, Usage> used = usage.usageSince(day, week, month);
			List<SnapshotLimitUsage> limits = new ArrayList<>();
			for (SnapshotLimit rule : snapshot.limits()) {
				Usage u = used.getOrDefault(rule.vehicleId(), Usage.NONE);
				boolean volume = rule.kind() == LimitKind.VOLUME;
				long spent = switch (rule.period()) {
					case DAY -> volume ? u.dayMl() : u.dayPiastres();
					case WEEK -> volume ? u.weekMl() : u.weekPiastres();
					case MONTH -> volume ? u.monthMl() : u.monthPiastres();
				};
				Instant start = switch (rule.period()) {
					case DAY -> day;
					case WEEK -> week;
					case MONTH -> month;
				};
				limits.add(new SnapshotLimitUsage(rule.vehicleId(), rule.kind(), rule.period(), rule.maxValue(), spent, start));
			}

			Set<UUID> companyIds = new HashSet<>();
			snapshot.vehicles().forEach((v) -> companyIds.add(v.companyId()));
			Map<UUID, Long> balances = wallets.balances(companyIds);
			List<SnapshotCompany> companyList = companies.findAll(companyIds)
				.stream()
				.map((c) -> company(c, balances.getOrDefault(c.id(), 0L)))
				.toList();

			return new PosSnapshot(now, ApiContract.POS_CONTRACT, vipEnabled,
					new SnapshotStation(station.id(), station.nameAr(), station.nameEn(), zone.getId(), station.fuelTypes(),
							station.active()),
					List.copyOf(schedule), Base64.getEncoder().encodeToString(tagKey), snapshot.tags(), snapshot.vehicles(),
					List.copyOf(limits), companyList);
		});
	}

	/** The wallet as a word and a capped allowance: enough to decide offline, never the balance. */
	private SnapshotCompany company(CompanyRef c, long balance) {
		SnapshotWalletStatus status = !c.active() ? SnapshotWalletStatus.SUSPENDED
				: balance <= 0 ? SnapshotWalletStatus.INSUFFICIENT
						: balance < c.lowBalanceThresholdPiastres() ? SnapshotWalletStatus.LOW : SnapshotWalletStatus.AVAILABLE;
		long allowance = c.active() ? Math.max(0, Math.min(balance, allowanceCap)) : 0;
		return new SnapshotCompany(c.id(), c.nameAr(), c.nameEn(), c.active(), c.cooldownMinutes(), status, allowance);
	}

	/** This device's tag-digest key: a different key per device, so one device's digests are useless on another. */
	byte[] tagKey(UUID deviceId) {
		return HexFormat.of().parseHex(keys.hash(TAG_KEY_NAMESPACE, deviceId.toString()));
	}

	private <T> T asPlatform(Supplier<T> work) {
		return DbScope.runAs(new DbScope.Context(Scope.PLATFORM, null), () -> tx.execute((status) -> work.get()));
	}

}
