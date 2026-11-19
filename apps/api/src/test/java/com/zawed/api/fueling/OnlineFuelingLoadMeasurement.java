package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.Random;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

import javax.sql.DataSource;

import com.jayway.jsonpath.JsonPath;
import com.zaxxer.hikari.HikariDataSource;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledIfSystemProperty;
import org.springframework.beans.factory.annotation.Autowired;

import com.zawed.api.platform.ratelimit.InMemoryRateLimiter;
import com.zawed.api.support.Owner;

/**
 * An executed load measurement inside the test JVM: concurrent online fuelings through the real
 * HTTP API (device token, agent session, {@code POST /pos/fuelings}) on the embedded PostgreSQL.
 * Never part of the normal suite: the class name does not match the Surefire patterns and the test
 * runs only with {@code -Dzawed.perf=true}.
 *
 * <pre>
 * mvnw test -Dtest=OnlineFuelingLoadMeasurement -Dzawed.perf=true
 * </pre>
 *
 * <p>{@value #THREADS_DEFAULT} threads each record {@value #PER_THREAD_DEFAULT} fuelings, every one on
 * its own vehicle (a tag fuels once per cooldown). Three in four go to 100 "spread" companies; one in
 * four goes to one shared "hot" company, so every thread contends for that company's wallet lock at
 * the same time. Each thread alternates between two devices of its own (the POS limit is 120
 * requests a minute per device). The setup goes through the API too, and the vehicle and tag
 * creation latencies are measured on the way.
 *
 * <p>Afterwards every company's wallet must equal what was credited minus what was charged, equal
 * the sum of its ledger entries, every station payable must equal the sum of its entries, the
 * payables together must equal the total charged, and PostgreSQL must report no deadlock. The numbers
 * go to {@code target/perf/load-measurement.md}.
 */
@EnabledIfSystemProperty(named = "zawed.perf", matches = "true")
class OnlineFuelingLoadMeasurement extends PosTestSupport {

	static final int THREADS_DEFAULT = 20;
	static final int PER_THREAD_DEFAULT = 200;

	private static final int THREADS = Integer.getInteger("zawed.perf.load.threads", THREADS_DEFAULT);
	private static final int PER_THREAD = Integer.getInteger("zawed.perf.load.perThread", PER_THREAD_DEFAULT);
	private static final int SPREAD_COMPANIES = 100;
	private static final int OPERATORS = 5;
	private static final int STATIONS_PER_OPERATOR = 2;
	private static final int SETUP_THREADS = 8;
	private static final int WARM_UP_PER_THREAD = 10;
	private static final long VOLUME_ML = 20_000; // 20 L at 15 EGP = 300 EGP = 30,000 piastres
	private static final long AMOUNT = VOLUME_ML * PRICE / 1_000;
	private static final long SPREAD_CREDIT = 5_000_000;
	private static final long HOT_CREDIT = 50_000_000;
	private static final long WARM_UP_CREDIT = 50_000_000;

	@Autowired
	InMemoryRateLimiter rates;

	@Autowired
	DataSource dataSource;

	private final AtomicInteger setupCalls = new AtomicInteger();

	record Sample(long nanos, boolean hot, int status, String outcome) {
	}

	@Test
	void concurrentOnlineFuelingsKeepTheMoneyExact() throws Exception {
		StringBuilder report = new StringBuilder("# Online fueling load measurement\n\n");
		long setupStarted = System.nanoTime();

		// ------------------------------------------------------------------------- the network
		List<String> stations = new ArrayList<>();
		List<String> stationOperators = new ArrayList<>();
		for (int o = 0; o < OPERATORS; o++) {
			String operator = operator();
			for (int s = 0; s < STATIONS_PER_OPERATOR; s++) {
				String station = station(operator, "GAS_92");
				price(station, "GAS_92", PRICE);
				stations.add(station);
				stationOperators.add(operator);
			}
		}
		List<Pump> pumps = new ArrayList<>();
		for (int p = 0; p < THREADS * 2; p++) {
			rates.reset(); // activation is limited to 10 a minute per address, and every pump comes from 127.0.0.1
			int station = p % stations.size();
			pumps.add(pumpAt(stationOperators.get(station), stations.get(station)));
		}

		// ------------------------------------------------------------------------- the fleets
		List<String> spread = new ArrayList<>();
		for (int c = 0; c < SPREAD_COMPANIES; c++) {
			String company = seedCompany().toString();
			credit(company, SPREAD_CREDIT);
			spread.add(company);
		}
		String hot = seedCompany().toString();
		credit(hot, HOT_CREDIT);
		String warmUp = seedCompany().toString();
		credit(warmUp, WARM_UP_CREDIT);

		int total = THREADS * PER_THREAD;
		int hotCount = (int) java.util.stream.IntStream.range(0, PER_THREAD).filter(OnlineFuelingLoadMeasurement::isHot)
			.count() * THREADS;
		int spreadCount = total - hotCount;
		List<String> spreadOwners = new ArrayList<>();
		for (int i = 0; i < spreadCount; i++) {
			spreadOwners.add(spread.get(i % SPREAD_COMPANIES));
		}
		List<String> hotOwners = Collections.nCopies(hotCount, hot);
		List<String> warmUpOwners = Collections.nCopies(THREADS * WARM_UP_PER_THREAD, warmUp);

		List<Long> vehicleNanos = Collections.synchronizedList(new ArrayList<>());
		List<Long> tagNanos = Collections.synchronizedList(new ArrayList<>());
		List<Car> spreadCars = cars(spreadOwners, vehicleNanos, tagNanos);
		List<Car> hotCars = cars(hotOwners, vehicleNanos, tagNanos);
		List<Car> warmUpCars = cars(warmUpOwners, vehicleNanos, tagNanos);
		Collections.shuffle(spreadCars, new Random(42)); // each thread meets many companies, and some meet each other
		double setupSeconds = (System.nanoTime() - setupStarted) / 1e9;

		// -------------------------------------------------------------------------- warm-up
		rates.reset();
		run(THREADS, (t) -> {
			for (int i = 0; i < WARM_UP_PER_THREAD; i++) {
				HttpResponse<String> r = fuel(pumps.get(2 * t + i % 2), warmUpCars.get(t * WARM_UP_PER_THREAD + i), VOLUME_ML);
				assertThat(r.statusCode()).as(r.body()).isEqualTo(201);
			}
			return List.of();
		});

		// ---------------------------------------------------------------------- measurement
		rates.reset();
		long deadlocksBefore = deadlocks();
		long started = System.nanoTime();
		List<List<Sample>> perThread = run(THREADS, (t) -> {
			List<Sample> samples = new ArrayList<>();
			int hotIndex = 0;
			int spreadIndex = 0;
			int hotPerThread = hotCount / THREADS;
			int spreadPerThread = spreadCount / THREADS;
			for (int i = 0; i < PER_THREAD; i++) {
				boolean isHot = isHot(i);
				Car car = isHot ? hotCars.get(t * hotPerThread + hotIndex++) : spreadCars.get(t * spreadPerThread + spreadIndex++);
				long t0 = System.nanoTime();
				HttpResponse<String> r = fuel(pumps.get(2 * t + i % 2), car, VOLUME_ML);
				long nanos = System.nanoTime() - t0;
				String outcome = r.statusCode() == 201 ? JsonPath.read(r.body(), "$.status") : code(r);
				samples.add(new Sample(nanos, isHot, r.statusCode(), outcome));
			}
			return samples;
		});
		double wallSeconds = (System.nanoTime() - started) / 1e9;
		Thread.sleep(1_500); // cumulative statistics are flushed by each backend about once a second
		long deadlocks = deadlocks() - deadlocksBefore;

		List<Sample> all = perThread.stream().flatMap(List::stream).toList();
		long completed = all.stream().filter((s) -> s.status() == 201 && "COMPLETED".equals(s.outcome())).count();
		long serverErrors = all.stream().filter((s) -> s.status() >= 500).count();

		// ------------------------------------------------------------------------- the money
		List<String> companies = new ArrayList<>(spread);
		companies.add(hot);
		companies.add(warmUp);
		long totalCharged = 0;
		List<String> mismatches = new ArrayList<>();
		for (String company : companies) {
			UUID id = UUID.fromString(company);
			long credited = company.equals(hot) ? HOT_CREDIT : company.equals(warmUp) ? WARM_UP_CREDIT : SPREAD_CREDIT;
			long charged = Owner.<Long>queryOne(
					"select coalesce(sum(amount_piastres), 0)::bigint from fuelings where company_id = ? and ledger_transaction_id is not null",
					id);
			long balance = Owner.<Long>queryOne("select balance_piastres from company_wallets where company_id = ?", id);
			long entries = Owner.<Long>queryOne("""
					select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
					from ledger_entries e join company_wallets w on w.account_id = e.account_id where w.company_id = ?
					""", id);
			long lastBalanceAfter = Owner.<Long>queryOne("""
					select e.balance_after_piastres from ledger_entries e join company_wallets w on w.account_id = e.account_id
					where w.company_id = ? order by e.seq desc limit 1
					""", id);
			if (balance != credited - charged || balance != entries || balance != lastBalanceAfter) {
				mismatches.add(company + ": credited " + credited + ", charged " + charged + ", balance " + balance
						+ ", entries " + entries + ", last balance_after " + lastBalanceAfter);
			}
			totalCharged += charged;
		}
		long payablesCached = 0;
		long payablesFromEntries = 0;
		for (String station : stations) {
			UUID id = UUID.fromString(station);
			long cached = Owner.<Long>queryOne("select balance_piastres from station_payables where station_id = ?", id);
			long fromEntries = Owner.<Long>queryOne("""
					select coalesce(sum(case e.direction when 'CREDIT' then e.amount_piastres else -e.amount_piastres end), 0)::bigint
					from ledger_entries e join station_payables p on p.account_id = e.account_id where p.station_id = ?
					""", id);
			if (cached != fromEntries) {
				mismatches.add("station " + station + ": payable " + cached + ", entries " + fromEntries);
			}
			payablesCached += cached;
			payablesFromEntries += fromEntries;
		}
		long fuelingRows = Owner.<Long>queryOne("select count(*) from fuelings where company_id = ?::uuid or company_id = any(?::uuid[])",
				hot, spread.toArray(new String[0]));
		long unbalanced = Owner.<Long>queryOne("""
				select count(*) from (
				  select t.id from ledger_transactions t join ledger_entries e on e.transaction_id = t.id
				  where t.source_type = 'FUELING'
				  group by t.id
				  having sum(case e.direction when 'DEBIT' then e.amount_piastres else 0 end)
				      <> sum(case e.direction when 'CREDIT' then e.amount_piastres else 0 end)) x
				""");

		// ------------------------------------------------------------------------------ report
		int poolSize = dataSource instanceof HikariDataSource hikari ? hikari.getMaximumPoolSize() : -1;
		report.append("Run ").append(java.time.Instant.now()).append(" on the embedded PostgreSQL of the test suite ")
			.append("(Zonky, ").append(Owner.<String>queryOne("select version()")).append("), ")
			.append(System.getProperty("java.vm.name")).append(' ').append(System.getProperty("java.runtime.version"))
			.append(", ").append(Runtime.getRuntime().availableProcessors()).append(" logical processors. ")
			.append("Client, API and database share one developer laptop: this is not production hardware.\n\n");
		report.append("## Setup\n\n");
		report.append(String.format(Locale.ROOT,
				"- %d operators, %d stations, %d devices (each activated with its own P-256 key, a signed token, an agent "
						+ "signed in and the PIN changed), %d spread companies + 1 hot company + 1 warm-up company, %d vehicles "
						+ "with a tag, created through the API in %.1f s (%d setup calls).%n",
				OPERATORS, stations.size(), pumps.size(), SPREAD_COMPANIES, spreadCars.size() + hotCars.size() + warmUpCars.size(),
				setupSeconds, setupCalls.get()));
		report.append(String.format(Locale.ROOT, "- HikariCP maximum pool size: %d (the application default). %d request threads.%n",
				poolSize, THREADS));
		report.append(String.format(Locale.ROOT,
				"- Vehicle creation (`POST /ops/vehicles`, %d concurrent callers, n=%d): %s%n", SETUP_THREADS,
				vehicleNanos.size(), percentiles(vehicleNanos)));
		report.append(String.format(Locale.ROOT,
				"- Tag issue (`POST /ops/vehicles/{id}/tags`, %d concurrent callers, n=%d): %s%n%n", SETUP_THREADS,
				tagNanos.size(), percentiles(tagNanos)));
		report.append("## Online fuelings\n\n");
		report.append(String.format(Locale.ROOT,
				"- %d threads × %d fuelings = %d requests after a warm-up of %d; %d to the hot company (one wallet), %d to %d "
						+ "spread companies.%n",
				THREADS, PER_THREAD, total, THREADS * WARM_UP_PER_THREAD, hotCount, spreadCount, SPREAD_COMPANIES));
		report.append(String.format(Locale.ROOT, "- Wall time %.2f s, throughput %.1f fuelings/s.%n", wallSeconds,
				total / wallSeconds));
		report.append("- Latency, all: ").append(percentiles(all.stream().map(Sample::nanos).toList())).append('\n');
		report.append("- Latency, hot company: ")
			.append(percentiles(all.stream().filter(Sample::hot).map(Sample::nanos).toList()))
			.append('\n');
		report.append("- Latency, spread companies: ")
			.append(percentiles(all.stream().filter((s) -> !s.hot()).map(Sample::nanos).toList()))
			.append('\n');
		report.append(String.format(Locale.ROOT, "- Outcomes: %d completed, %d server errors, other: %s.%n", completed,
				serverErrors,
				all.stream().filter((s) -> !(s.status() == 201 && "COMPLETED".equals(s.outcome())))
					.map((s) -> s.status() + " " + s.outcome())
					.distinct()
					.toList()));
		report.append(String.format(Locale.ROOT, "- Deadlocks reported by PostgreSQL during the run: %d.%n%n", deadlocks));
		report.append("## Money checks\n\n");
		report.append(String.format(Locale.ROOT,
				"- %d wallets: balance = credited − charged = sum of ledger entries = last balance_after: %s.%n",
				companies.size(), mismatches.isEmpty() ? "all equal" : mismatches.size() + " mismatches"));
		report.append(String.format(Locale.ROOT,
				"- %d station payables: cached %d = from entries %d; total charged %d piastres (expected %d × %d = %d).%n",
				stations.size(), payablesCached, payablesFromEntries, totalCharged,
				total + THREADS * WARM_UP_PER_THREAD, AMOUNT, (long) (total + THREADS * WARM_UP_PER_THREAD) * AMOUNT));
		report.append(String.format(Locale.ROOT, "- Unbalanced fueling transactions: %d. Fueling rows for the measured companies: %d.%n",
				unbalanced, fuelingRows));
		Path out = Path.of("target", "perf");
		Files.createDirectories(out);
		Files.writeString(out.resolve("load-measurement.md"), report.toString(), StandardCharsets.UTF_8);
		System.out.println(report);

		assertThat(serverErrors).as("server errors").isZero();
		assertThat(completed).as("completed fuelings").isEqualTo(total);
		assertThat(mismatches).as("money mismatches").isEmpty();
		assertThat(payablesCached).isEqualTo(payablesFromEntries).isEqualTo(totalCharged);
		assertThat(totalCharged).isEqualTo((long) (total + THREADS * WARM_UP_PER_THREAD) * AMOUNT);
		assertThat(unbalanced).isZero();
		assertThat(fuelingRows).isEqualTo(total);
		assertThat(deadlocks).as("deadlocks").isZero();
	}

	/** One fueling in four goes to the hot company. */
	private static boolean isHot(int index) {
		return index % 4 == 0;
	}

	/** Vehicles with a tag for the given owners, created through the API by several callers at once. */
	private List<Car> cars(List<String> owners, List<Long> vehicleNanos, List<Long> tagNanos) throws Exception {
		Car[] cars = new Car[owners.size()];
		AtomicInteger next = new AtomicInteger();
		run(SETUP_THREADS, (t) -> {
			for (int i = next.getAndIncrement(); i < owners.size(); i = next.getAndIncrement()) {
				cars[i] = timedCar(owners.get(i), vehicleNanos, tagNanos);
			}
			return List.of();
		});
		return new ArrayList<>(Arrays.asList(cars));
	}

	/** {@link #car(String)} with each of its two calls timed. */
	private Car timedCar(String company, List<Long> vehicleNanos, List<Long> tagNanos) throws Exception {
		if (setupCalls.addAndGet(2) % 200 < 2) {
			rates.reset(); // one Ops user may make 300 calls a minute; the setup makes thousands
		}
		long t0 = System.nanoTime();
		HttpResponse<String> vehicle = authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"LT " + UUID.randomUUID().toString().substring(0, 8)
					.toUpperCase() + "\",\"make\":\"Toyota\",\"model\":\"Hilux\",\"fuelType\":\"GAS_92\","
						+ "\"tankCapacityMl\":60000,\"tier\":\"NORMAL\"}}");
		vehicleNanos.add(System.nanoTime() - t0);
		assertThat(vehicle.statusCode()).as(vehicle.body()).isEqualTo(201);
		String vehicleId = JsonPath.read(vehicle.body(), "$.id");
		String uid = randomUid();
		long t1 = System.nanoTime();
		HttpResponse<String> tag = authed("POST", "/api/v1/ops/vehicles/" + vehicleId + "/tags", ops.accessToken(),
				"{\"uid\":\"" + uid + "\"}");
		tagNanos.add(System.nanoTime() - t1);
		assertThat(tag.statusCode()).as(tag.body()).isEqualTo(201);
		return new Car(company, vehicleId, uid);
	}

	@FunctionalInterface
	interface Work<T> {

		List<T> run(int thread) throws Exception;

	}

	/** Runs {@code n} workers released together and waits for all of them (up to ten minutes). */
	private static <T> List<List<T>> run(int n, Work<T> work) throws Exception {
		CountDownLatch start = new CountDownLatch(1);
		try (ExecutorService pool = Executors.newFixedThreadPool(n)) {
			List<Future<List<T>>> futures = new ArrayList<>();
			for (int i = 0; i < n; i++) {
				int index = i;
				futures.add(pool.submit(() -> {
					start.await();
					return work.run(index);
				}));
			}
			start.countDown();
			List<List<T>> results = new ArrayList<>();
			for (Future<List<T>> future : futures) {
				results.add(future.get(10, TimeUnit.MINUTES));
			}
			return results;
		}
	}

	private static long deadlocks() {
		return Owner.<Long>queryOne("select deadlocks from pg_stat_database where datname = current_database()");
	}

	private static String percentiles(List<Long> nanos) {
		if (nanos.isEmpty()) {
			return "no samples";
		}
		long[] sorted = nanos.stream().mapToLong(Long::longValue).sorted().toArray();
		return String.format(Locale.ROOT, "p50 %.1f ms, p95 %.1f ms, p99 %.1f ms, max %.1f ms", at(sorted, 0.50),
				at(sorted, 0.95), at(sorted, 0.99), sorted[sorted.length - 1] / 1e6);
	}

	/** Nearest-rank percentile, in milliseconds. */
	private static double at(long[] sorted, double p) {
		int rank = (int) Math.ceil(p * sorted.length);
		return sorted[Math.max(0, Math.min(sorted.length - 1, rank - 1))] / 1e6;
	}

}
