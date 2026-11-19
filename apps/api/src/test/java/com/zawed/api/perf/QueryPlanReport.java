package com.zawed.api.perf;

import static org.assertj.core.api.Assertions.assertThat;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.DayOfWeek;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDate;
import java.time.YearMonth;
import java.time.ZoneId;
import java.time.temporal.TemporalAdjusters;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Collectors;
import java.util.stream.Stream;

import io.zonky.test.db.postgres.embedded.EmbeddedPostgres;

import org.flywaydb.core.Flyway;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledIfSystemProperty;

/**
 * Measured evidence for the read paths on {@code fuelings} at the target scale (production readiness
 * audit F6 and §7.2). Never part of the normal suite: the class name does not match the Surefire
 * patterns and the test runs only with {@code -Dzawed.perf=true}.
 *
 * <pre>
 * mvnw test -Dtest=QueryPlanReport -Dzawed.perf=true -Dzawed.perf.dataDir=D:/zawed-perf/pgdata
 * </pre>
 *
 * <p>What it does:
 * <ol>
 * <li>Starts its own embedded PostgreSQL (same binaries as the suite) with its data directory where
 * {@code zawed.perf.dataDir} says, because the dataset needs several GB that the system temporary
 * directory may not have. It applies the Flyway migrations up to V11 as the owner.</li>
 * <li>Loads a synthetic dataset with set-based SQL ({@code generate_series}): companies, vehicles
 * with one active tag each, operators, stations, devices, agents, fuelings over twelve months and a
 * few alerts. The fuelings same-tenant trigger is disabled for the bulk load only (foreign keys and
 * CHECK constraints stay enforced), then re-enabled, and the same rule is verified set-based.</li>
 * <li>For each phase (a set of candidate indexes), runs the SQL the API runs, copied from
 * {@code Fuelings}, {@code Activity}, {@code FuelingExports} and {@code FuelingUsageReader}, as
 * {@code zawed_app} with the scope set exactly as {@code ScopedTransactionManager} sets it, so
 * row-level security is part of every plan. Each query is prepared with its parameters and executed
 * as a custom plan (what PostgreSQL does for these statements; see the report), once to warm up,
 * then {@code EXPLAIN (ANALYZE, BUFFERS)}, then timed.</li>
 * <li>Measures the write cost of each index set: single-row inserts and one bulk insert in a
 * transaction that is rolled back.</li>
 * <li>The {@code v16} phase applies the real migration V16 through Flyway on the loaded table and
 * measures again; {@code v16_concurrent} applies the same indexes with {@code CREATE INDEX
 * CONCURRENTLY} outside a transaction and Flyway's session-level lock (see V16 for why the committed
 * script does not). Queries named {@code proposal_*} in the output are a candidate rewrite of the list
 * query, not the API's SQL.</li>
 * </ol>
 * Everything lands in {@code target/perf/}: {@code summary.md}, {@code environment.txt} and one plan
 * file per phase and query under {@code plans/}.
 */
@EnabledIfSystemProperty(named = "zawed.perf", matches = "true")
class QueryPlanReport {

	// ------------------------------------------------------------------------------ configuration

	private static final int COMPANIES = Integer.getInteger("zawed.perf.companies", 100);
	private static final int VEHICLES = Integer.getInteger("zawed.perf.vehicles", 200_000);
	private static final int OPERATORS = Integer.getInteger("zawed.perf.operators", 20);
	private static final int STATIONS = Integer.getInteger("zawed.perf.stations", 300);
	private static final int DEVICES = Integer.getInteger("zawed.perf.devices", 1_000);
	private static final int AGENTS = Integer.getInteger("zawed.perf.agents", 2_000);
	private static final int FUELINGS = Integer.getInteger("zawed.perf.fuelings", 5_000_000);
	private static final int CHUNK = Integer.getInteger("zawed.perf.chunk", 250_000);
	private static final int RUNS = Integer.getInteger("zawed.perf.runs", 5);
	/** Keep and reuse an already loaded data directory (same sizes) instead of starting from nothing. */
	private static final boolean REUSE = Boolean.getBoolean("zawed.perf.reuse");
	private static final List<String> PHASES = List
		.of(System.getProperty("zawed.perf.phases", "baseline,brin,time,composite,composite_brin,company_operator_brin,v16,v16_concurrent").split(","));
	private static final Path DATA = Path.of(System.getProperty("zawed.perf.dataDir", "target/perf/pgdata"));
	private static final Path OUT = Path.of("target", "perf");
	private static final ZoneId CAIRO = ZoneId.of("Africa/Cairo");

	/** Non-default server settings, recorded in the report. Everything else is the PostgreSQL default. */
	private static final Map<String, String> SERVER = new LinkedHashMap<>();
	static {
		SERVER.put("shared_buffers", System.getProperty("zawed.perf.sharedBuffers", "512MB"));
		SERVER.put("maintenance_work_mem", "256MB");
		SERVER.put("max_wal_size", "4GB");
		SERVER.put("checkpoint_timeout", "15min");
	}

	// --------------------------------------------------------------------------------- candidates

	record Index(String name, String ddl) {
	}

	private static final Index BRIN = new Index("fuelings_perf_evaluated_at_brin",
			"create index fuelings_perf_evaluated_at_brin on fuelings using brin (evaluated_at)");
	private static final Index TIME = new Index("fuelings_perf_evaluated_at_idx",
			"create index fuelings_perf_evaluated_at_idx on fuelings (evaluated_at)");
	private static final Index COMPANY_TIME = new Index("fuelings_perf_company_time_idx",
			"create index fuelings_perf_company_time_idx on fuelings (company_id, evaluated_at)");
	private static final Index OPERATOR_TIME = new Index("fuelings_perf_operator_time_idx",
			"create index fuelings_perf_operator_time_idx on fuelings (operator_id, evaluated_at)");
	private static final Index STATION_TIME = new Index("fuelings_perf_station_time_idx",
			"create index fuelings_perf_station_time_idx on fuelings (station_id, evaluated_at)");

	private static final Map<String, List<Index>> CANDIDATES = new LinkedHashMap<>();
	static {
		CANDIDATES.put("baseline", List.of());
		CANDIDATES.put("brin", List.of(BRIN));
		CANDIDATES.put("time", List.of(TIME));
		CANDIDATES.put("composite", List.of(COMPANY_TIME, OPERATOR_TIME, STATION_TIME));
		CANDIDATES.put("composite_brin", List.of(COMPANY_TIME, OPERATOR_TIME, STATION_TIME, BRIN));
		CANDIDATES.put("company_operator_brin", List.of(COMPANY_TIME, OPERATOR_TIME, BRIN));
	}

	private static final List<Index> ALL_CANDIDATES = List.of(BRIN, TIME, COMPANY_TIME, OPERATOR_TIME, STATION_TIME);

	/** The fuelings columns in table order, for the inserts of the write-cost measurement. */
	private static final String COLUMNS = "id, client_txn_id, reference, status, decline_reason, origin, company_id, "
			+ "vehicle_id, tag_id, operator_id, station_id, device_id, agent_id, fuel_type, volume_ml, unit_price_piastres, "
			+ "amount_piastres, billable, evaluated_at, device_at, received_at, receipt_locale, plate, company_name_ar, "
			+ "company_name_en, station_name_ar, station_name_en, agent_worker_id, ledger_transaction_id, conflicts, "
			+ "payable_held";

	// ---------------------------------------------------------------------------------------- run

	private final StringBuilder summary = new StringBuilder();
	private final StringBuilder environment = new StringBuilder();

	@Test
	void report() throws Exception {
		Files.createDirectories(OUT.resolve("plans"));
		if (!REUSE) {
			deleteRecursively(DATA);
		}
		Files.createDirectories(DATA.getParent() == null ? Path.of(".") : DATA.getParent());
		EmbeddedPostgres.Builder builder = EmbeddedPostgres.builder()
			.setDataDirectory(DATA)
			.setCleanDataDirectory(false)
			.setPGStartupWait(Duration.ofMinutes(5));
		SERVER.forEach(builder::setServerConfig);
		try (EmbeddedPostgres pg = builder.start()) {
			String url = pg.getJdbcUrl("postgres", "postgres");
			try (Connection owner = DriverManager.getConnection(url, "postgres", "postgres")) {
				undoV16(owner);
			}
			migrate(url, "11");
			try (Connection owner = DriverManager.getConnection(url, "postgres", "postgres")) {
				owner.setAutoCommit(true);
				recordEnvironment(owner, url);
				if (!loaded(owner)) {
					load(owner);
				}
				else {
					log("Reusing the loaded dataset in " + DATA.toAbsolutePath());
				}
				Subjects subjects = subjects(owner);
				recordDataset(owner, subjects);
				prepareWriteSamples(owner, subjects);
				summary.append("# Query plan report\n\nGenerated ").append(Instant.now()).append(" by QueryPlanReport.\n\n");
				summary.append(environment).append('\n');
				for (String phase : PHASES) {
					runPhase(url, owner, phase.trim(), subjects);
					write("summary.md", summary.toString());
				}
			}
		}
		write("summary.md", summary.toString());
		log("Report written to " + OUT.toAbsolutePath());
	}

	// -------------------------------------------------------------------------------- migrations

	private static void migrate(String url, String target) {
		var config = Flyway.configure().dataSource(url, "postgres", "postgres").locations("classpath:db/migration");
		if (target != null) {
			config.target(target);
		}
		config.load().migrate();
	}

	/**
	 * V16 as CREATE INDEX CONCURRENTLY outside a transaction, the way it would have to be written with
	 * Flyway's session-level lock ({@code postgresql.transactional.lock=false}): the migrations are
	 * copied to a scratch directory, V16 rewritten with CONCURRENTLY and given its .conf, and Flyway
	 * pointed there. With the default transactional lock the same script never finishes (see V16).
	 */
	private static void migrateConcurrently(String url) throws IOException {
		Path dir = OUT.resolve("cic-migrations");
		deleteRecursively(dir);
		Files.createDirectories(dir);
		try (Stream<Path> files = Files.list(Path.of("src", "main", "resources", "db", "migration"))) {
			for (Path file : files.toList()) {
				String name = file.getFileName().toString();
				if (!name.startsWith("V16__")) {
					Files.copy(file, dir.resolve(name));
				}
				else if (name.endsWith(".sql")) {
					Files.writeString(dir.resolve(name), Files.readString(file)
						.replace("CREATE INDEX IF NOT EXISTS", "CREATE INDEX CONCURRENTLY IF NOT EXISTS"));
					Files.writeString(dir.resolve(name + ".conf"), "executeInTransaction=false" + System.lineSeparator());
				}
			}
		}
		Flyway.configure()
			.dataSource(url, "postgres", "postgres")
			.locations("filesystem:" + dir.toAbsolutePath())
			.configuration(Map.of("flyway.postgresql.transactional.lock", "false"))
			.load()
			.migrate();
	}

	/** The indexes V16 creates, read from the migration itself (empty while V16 does not exist). */
	private static List<String> v16Indexes() throws IOException {
		Path dir = Path.of("src", "main", "resources", "db", "migration");
		List<String> names = new ArrayList<>();
		try (Stream<Path> files = Files.list(dir)) {
			for (Path file : files.filter((f) -> f.getFileName().toString().startsWith("V16__")
					&& f.getFileName().toString().endsWith(".sql")).toList()) {
				Matcher m = Pattern.compile("(?i)create\\s+index\\s+(?:concurrently\\s+)?if\\s+not\\s+exists\\s+(\\w+)")
					.matcher(Files.readString(file).replaceAll("--[^\\n]*", ""));
				while (m.find()) {
					names.add(m.group(1));
				}
			}
		}
		return names;
	}

	/** A reused data directory may have V16 applied by an earlier run: take it back off (scratch database). */
	private static void undoV16(Connection owner) throws Exception {
		if (!exists(owner, "select 1 from pg_tables where tablename = 'flyway_schema_history'")) {
			return;
		}
		for (String index : v16Indexes()) {
			exec(owner, "drop index if exists " + index);
		}
		exec(owner, "delete from flyway_schema_history where version = '16'");
	}

	// ------------------------------------------------------------------------------- environment

	private void recordEnvironment(Connection owner, String url) throws Exception {
		environment.append("## Environment\n\n");
		environment.append("- PostgreSQL: ").append(queryString(owner, "select version()")).append('\n');
		environment.append("- JVM: ")
			.append(System.getProperty("java.vm.name"))
			.append(' ')
			.append(System.getProperty("java.runtime.version"))
			.append('\n');
		environment.append("- OS: ")
			.append(System.getProperty("os.name"))
			.append(' ')
			.append(System.getProperty("os.version"))
			.append(", ")
			.append(Runtime.getRuntime().availableProcessors())
			.append(" logical processors\n");
		environment.append("- Data directory: ").append(DATA.toAbsolutePath()).append('\n');
		environment.append("- Embedded PostgreSQL (Zonky) on a developer laptop, not production hardware.\n");
		environment.append("- Settings:");
		for (String name : List.of("shared_buffers", "work_mem", "maintenance_work_mem", "effective_cache_size",
				"random_page_cost", "max_parallel_workers_per_gather", "jit", "synchronous_commit", "fsync",
				"max_wal_size", "plan_cache_mode")) {
			environment.append(' ').append(name).append('=').append(queryString(owner, "show " + name)).append(';');
		}
		environment.append('\n');
		write("environment.txt", environment.toString());
	}

	// ------------------------------------------------------------------------------------ dataset

	private static boolean loaded(Connection owner) throws SQLException {
		return exists(owner, "select 1 from pg_tables where schemaname = 'perf' and tablename = 'meta'")
				&& exists(owner, "select 1 from perf.meta where key = 'complete'");
	}

	private void load(Connection owner) throws Exception {
		long started = System.nanoTime();
		exec(owner, "drop schema if exists perf cascade");
		exec(owner, "create schema perf");
		exec(owner, "create table perf.meta (key text primary key, value text)");
		exec(owner, "select setseed(0.42)");

		// Companies. Their sizes are skewed: company n gets the vehicles whose u^2 falls in its slot,
		// so company 0 holds about 10 % of the fleet and the smallest about 0.5 %.
		timed("companies", () -> {
			exec(owner, "create table perf.c as select n, uuidv7() as id from generate_series(0, " + (COMPANIES - 1) + ") n");
			exec(owner, "create unique index on perf.c (n)");
			exec(owner, """
					insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes,
					                       low_balance_threshold_piastres, created_at, updated_at)
					select id, 'Perf Company ' || n, 'شركة الأداء ' || n, 'Perf Company ' || n, 'ACTIVE', 180, 0, now(), now()
					from perf.c
					""");
		});
		timed("vehicles and tags", () -> {
			exec(owner, "create table perf.v as select x.n, uuidv7() as id, uuidv7() as tag_id, c.id as company_id, "
					+ "(array['GAS_92','GAS_92','GAS_95','DIESEL','GAS_80'])[1 + x.n % 5] as fuel_type, "
					+ "'ق ب ' || x.n as plate "
					+ "from (select n, least(" + (COMPANIES - 1) + ", floor(" + COMPANIES + " * power(random(), 2)))::int as cn "
					+ "      from generate_series(1, " + VEHICLES + ") n) x join perf.c c on c.n = x.cn");
			exec(owner, "create unique index on perf.v (n)");
			exec(owner, """
					insert into vehicles (id, company_id, plate, plate_key, make, model, year, fuel_type, tank_capacity_ml,
					                      tier, status, created_at, updated_at)
					select id, company_id, plate, 'PERF' || n, 'Toyota', 'Hilux', 2022, fuel_type, 60000, 'NORMAL', 'ACTIVE',
					       now(), now()
					from perf.v order by n
					""");
			exec(owner, """
					insert into rfid_tags (id, company_id, vehicle_id, uid_encrypted, uid_lookup, uid_last4, status,
					                       created_at, updated_at)
					select tag_id, company_id, id, decode(md5('uid' || n), 'hex'), md5('lookup' || n), right(md5('uid' || n), 4),
					       'ACTIVE', now(), now()
					from perf.v order by n
					""");
		});
		timed("operators, stations, devices, agents", () -> {
			exec(owner, "create table perf.o as select n, uuidv7() as id from generate_series(0, " + (OPERATORS - 1) + ") n");
			exec(owner, """
					insert into station_operators (id, legal_name, name_ar, name_en, status, created_at, updated_at)
					select id, 'Perf Operator ' || n, 'مشغل ' || n, 'Perf Operator ' || n, 'ACTIVE', now(), now() from perf.o
					""");
			exec(owner, "create table perf.s as select s.n, uuidv7() as id, o.id as operator_id from generate_series(0, "
					+ (STATIONS - 1) + ") s(n) join perf.o o on o.n = s.n % " + OPERATORS);
			exec(owner, "create unique index on perf.s (n)");
			exec(owner, """
					insert into stations (id, operator_id, name_ar, name_en, time_zone, connectivity, fuel_types, status,
					                      created_at, updated_at)
					select id, operator_id, 'محطة ' || n, 'Perf Station ' || n, 'Africa/Cairo', 'NORMAL',
					       array['GAS_80','GAS_92','GAS_95','DIESEL'], 'ACTIVE', now(), now()
					from perf.s order by n
					""");
			exec(owner, "create table perf.d as select d.n, uuidv7() as id, s.id as station_id, s.operator_id "
					+ "from generate_series(0, " + (DEVICES - 1) + ") d(n) join perf.s s on s.n = d.n % " + STATIONS);
			exec(owner, "create unique index on perf.d (n)");
			exec(owner, """
					insert into pos_devices (id, operator_id, station_id, code, status, public_key, key_algorithm, created_at,
					                         updated_at)
					select id, operator_id, station_id, 'PERF-D' || n, 'ACTIVE', '\\x00'::bytea, 'EC_P256', now(), now()
					from perf.d order by n
					""");
			exec(owner, "create table perf.a as select a.n, uuidv7() as id, s.id as station_id, s.operator_id, "
					+ "'PW' || lpad(a.n::text, 6, '0') as worker_id from generate_series(0, " + (AGENTS - 1)
					+ ") a(n) join perf.s s on s.n = a.n % " + STATIONS);
			exec(owner, "create unique index on perf.a (n)");
			exec(owner, """
					insert into agents (id, operator_id, station_id, worker_id, full_name, pin_hash, must_change_pin, status,
					                    created_at, updated_at)
					select id, operator_id, station_id, worker_id, 'Perf Agent ' || n, 'not-a-hash', false, 'ACTIVE', now(), now()
					from perf.a order by n
					""");
		});

		// Fuelings: FUELINGS rows over the 365 days before the load, inserted in receipt order. The id
		// is a UUIDv7 of the receipt time (the application makes it at insert). Online fuelings are
		// evaluated at receipt; an offline one (10 %) was evaluated up to 24 hours earlier on the
		// device, so evaluated_at is not perfectly correlated with the physical order.
		exec(owner, "insert into perf.meta values ('end', (date_trunc('second', now()))::text)");
		String end = queryString(owner, "select value from perf.meta where key = 'end'");
		exec(owner, "alter table fuelings disable trigger fuelings_same_tenant");
		try {
			for (long lo = 1; lo <= FUELINGS; lo += CHUNK) {
				long hi = Math.min(FUELINGS, lo + CHUNK - 1);
				long chunkStart = System.nanoTime();
				exec(owner, "insert into fuelings (" + COLUMNS + ") " + generator(lo, hi, "'" + end + "'::timestamptz"));
				log(String.format(Locale.ROOT, "fuelings %,d..%,d loaded in %.1f s", lo, hi,
						(System.nanoTime() - chunkStart) / 1e9));
			}
		}
		finally {
			exec(owner, "alter table fuelings enable trigger fuelings_same_tenant");
		}
		// What the disabled trigger would have refused, checked for the whole table at once.
		long mismatched = queryLong(owner, """
				select count(*) from fuelings f
				left join vehicles v on v.id = f.vehicle_id and v.company_id = f.company_id
				left join rfid_tags t on t.id = f.tag_id and t.vehicle_id = f.vehicle_id and t.company_id = f.company_id
				left join stations s on s.id = f.station_id and s.operator_id = f.operator_id
				left join pos_devices d on d.id = f.device_id and d.station_id = f.station_id
				left join agents a on a.id = f.agent_id and a.station_id = f.station_id
				where v.id is null or t.id is null or s.id is null or d.id is null or a.id is null
				""");
		assertThat(mismatched).as("fuelings that break the same-tenant rule").isZero();

		// About 1 % of fuelings raised an alert (low balance); the activity calendar counts them.
		timed("alerts", () -> exec(owner, """
				insert into alerts (id, type, severity, status, company_id, operator_id, station_id, vehicle_id, fueling_id,
				                    details, company_visible, created_at, device_id)
				select uuidv7(), 'LOW_BALANCE', 'WARNING', 'OPEN', company_id, operator_id, station_id, vehicle_id, id,
				       '{}'::jsonb, true, received_at, device_id
				from fuelings where abs(hashtext(id::text)) % 100 = 0
				"""));
		timed("vacuum analyze", () -> {
			exec(owner, "vacuum (analyze) fuelings");
			exec(owner, "vacuum (analyze) alerts");
			exec(owner, "analyze");
		});
		exec(owner, "insert into perf.meta values ('complete', '" + Instant.now() + "')");
		exec(owner, "insert into perf.meta values ('load_seconds', '"
				+ Math.round((System.nanoTime() - started) / 1e9) + "')");
		log(String.format(Locale.ROOT, "dataset loaded in %.0f s", (System.nanoTime() - started) / 1e9));
	}

	/**
	 * Rows {@code lo..hi} of the synthetic history: every column of {@code fuelings} in {@link #COLUMNS}
	 * order. Row g is received at {@code end - 365 days + g * 365 days / FUELINGS}.
	 */
	private static String generator(long lo, long hi, String end) {
		int devicesPerStation = Math.max(1, DEVICES / STATIONS);
		int agentsPerStation = Math.max(1, AGENTS / STATIONS);
		return """
				select x.id, gen_random_uuid() as client_txn_id, 'F-' || (10000 + x.g) as reference, x.status,
				       x.decline_reason, x.origin, v.company_id, v.id as vehicle_id, v.tag_id, s.operator_id,
				       s.id as station_id, d.id as device_id, a.id as agent_id, v.fuel_type, x.volume_ml,
				       x.price as unit_price_piastres, (x.volume_ml * x.price + 500) / 1000 as amount_piastres,
				       false as billable, x.evaluated_at, x.evaluated_at as device_at, x.received_at,
				       'ar'::text as receipt_locale, v.plate, co.name_ar as company_name_ar, co.name_en as company_name_en,
				       st.name_ar as station_name_ar, st.name_en as station_name_en, a.worker_id as agent_worker_id,
				       null::uuid as ledger_transaction_id,
				       case when x.origin = 'OFFLINE' and x.r4 < 0.2 then array['COOLDOWN'] else array[]::text[] end
				         as conflicts,
				       false as payable_held
				from (
				  select y.*,
				         case when y.status = 'COMPLETED' and y.r2 < 0.10 then 'OFFLINE' else 'ONLINE' end as origin,
				         case when y.status = 'COMPLETED' and y.r2 < 0.10 then y.received_at - y.r3 * interval '24 hours'
				              else y.received_at end as evaluated_at,
				         uuidv7(y.received_at - clock_timestamp()) as id
				  from (
				    select g,
				           %END% - interval '365 days' + (g::double precision / %N%) * interval '365 days' as received_at,
				           1 + floor(random() * %V%)::int as vn,
				           floor(random() * %S%)::int as sn,
				           floor(random() * %DPS%)::int as dk,
				           floor(random() * %APS%)::int as ak,
				           case when r1 < 0.05 then 'DECLINED' else 'COMPLETED' end as status,
				           case when r1 < 0.05 then (array['COOLDOWN','LIMIT_EXCEEDED','INSUFFICIENT_BALANCE','TAG_INACTIVE'])
				                [1 + floor(r1 * 80)::int] end as decline_reason,
				           r2, r3, r4,
				           5000 + floor(random() * 75000)::bigint as volume_ml,
				           (array[1500, 1700, 1900, 1350])[1 + floor(random() * 4)::int]::bigint as price
				    from (select g, random() as r1, random() as r2, random() as r3, random() as r4
				          from generate_series(%LO%, %HI%) g) r
				  ) y
				) x
				join perf.v v on v.n = x.vn
				join companies co on co.id = v.company_id
				join perf.s s on s.n = x.sn
				join stations st on st.id = s.id
				join perf.d d on d.n = x.sn + %S% * x.dk
				join perf.a a on a.n = x.sn + %S% * x.ak
				order by x.g
				""".replace("%END%", end)
			.replace("%N%", String.valueOf(FUELINGS))
			.replace("%V%", String.valueOf(VEHICLES))
			.replace("%S%", String.valueOf(STATIONS))
			.replace("%DPS%", String.valueOf(devicesPerStation))
			.replace("%APS%", String.valueOf(agentsPerStation))
			.replace("%LO%", String.valueOf(lo))
			.replace("%HI%", String.valueOf(hi));
	}

	// ----------------------------------------------------------------------------------- subjects

	/** The ids and time windows the queries use, chosen from the loaded data. */
	record Subjects(Instant end, UUID bigCompany, long bigCompanyVehicles, UUID medianCompany,
			long medianCompanyVehicles, UUID operator, UUID station, UUID vehicle, UUID offlineDevice,
			UUID offlineCompany, YearMonth oldMonth, YearMonth lastMonth) {

		Instant monthStart(YearMonth month) {
			return month.atDay(1).atStartOfDay(CAIRO).toInstant();
		}

		Instant monthEnd(YearMonth month) {
			return month.plusMonths(1).atDay(1).atStartOfDay(CAIRO).toInstant();
		}

	}

	private static Subjects subjects(Connection owner) throws SQLException {
		Instant end = Instant.parse(queryString(owner,
				"select to_char(value::timestamptz at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"') from perf.meta where key = 'end'"));
		List<Object[]> sizes = rows(owner,
				"select company_id, count(*) from vehicles where plate_key like 'PERF%' group by 1 order by 2 desc, 1");
		Object[] big = sizes.getFirst();
		Object[] median = sizes.get(sizes.size() / 2);
		UUID operator = (UUID) rows(owner, "select operator_id from perf.s where n = 0").getFirst()[0];
		UUID station = (UUID) rows(owner, "select id from perf.s where n = 0").getFirst()[0];
		UUID vehicle = (UUID) rows(owner,
				"select vehicle_id from fuelings where company_id = ? and status = 'COMPLETED' order by id desc limit 1",
				big[0])
			.getFirst()[0];
		List<Object[]> offline = rows(owner, """
				select device_id, company_id from fuelings
				where origin = 'OFFLINE' and evaluated_at > ?::timestamptz - interval '24 hours'
				group by 1, 2 order by count(*) desc, 1, 2 limit 1
				""", java.sql.Timestamp.from(end));
		UUID offlineDevice = offline.isEmpty() ? (UUID) rows(owner, "select id from perf.d where n = 0").getFirst()[0]
				: (UUID) offline.getFirst()[0];
		UUID offlineCompany = offline.isEmpty() ? (UUID) big[0] : (UUID) offline.getFirst()[1];
		YearMonth endMonth = YearMonth.from(end.atZone(CAIRO));
		return new Subjects(end, (UUID) big[0], (Long) big[1], (UUID) median[0], (Long) median[1], operator, station,
				vehicle, offlineDevice, offlineCompany, endMonth.minusMonths(6), endMonth.minusMonths(1));
	}

	private void recordDataset(Connection owner, Subjects s) throws Exception {
		StringBuilder d = new StringBuilder("## Dataset\n\n| Table | Rows | Heap + indexes |\n|---|---:|---:|\n");
		for (String table : List.of("companies", "vehicles", "rfid_tags", "station_operators", "stations", "pos_devices",
				"agents", "fuelings", "alerts")) {
			d.append("| ")
				.append(table)
				.append(" | ")
				.append(String.format(Locale.ROOT, "%,d", queryLong(owner, "select count(*) from " + table)))
				.append(" | ")
				.append(queryString(owner, "select pg_size_pretty(pg_total_relation_size('" + table + "'))"))
				.append(" |\n");
		}
		d.append("\n- fuelings heap only: ")
			.append(queryString(owner, "select pg_size_pretty(pg_relation_size('fuelings'))"))
			.append("; time span ")
			.append(queryString(owner, "select min(evaluated_at)::text || ' .. ' || max(evaluated_at)::text from fuelings"))
			.append('\n');
		d.append("- status/origin mix: ")
			.append(rows(owner, "select status || '/' || origin || '=' || count(*) from fuelings group by status, origin order by 1")
				.stream()
				.map((r) -> String.valueOf(r[0]))
				.collect(Collectors.joining(", ")))
			.append('\n');
		d.append("- largest company: ")
			.append(s.bigCompanyVehicles())
			.append(" vehicles, ")
			.append(String.format(Locale.ROOT, "%,d",
					queryLong(owner, "select count(*) from fuelings where company_id = ?", s.bigCompany())))
			.append(" fuelings; median company: ")
			.append(s.medianCompanyVehicles())
			.append(" vehicles, ")
			.append(String.format(Locale.ROOT, "%,d",
					queryLong(owner, "select count(*) from fuelings where company_id = ?", s.medianCompany())))
			.append(" fuelings\n");
		d.append("- operator used: ")
			.append(String.format(Locale.ROOT, "%,d",
					queryLong(owner, "select count(*) from fuelings where operator_id = ?", s.operator())))
			.append(" fuelings; station used: ")
			.append(String.format(Locale.ROOT, "%,d",
					queryLong(owner, "select count(*) from fuelings where station_id = ?", s.station())))
			.append(" fuelings\n");
		d.append("- windows: old month ")
			.append(s.oldMonth())
			.append(", last full month ")
			.append(s.lastMonth())
			.append(", dataset end ")
			.append(s.end())
			.append(" (all months in Africa/Cairo)\n");
		d.append("- load time: ")
			.append(queryString(owner, "select coalesce((select value from perf.meta where key = 'load_seconds'), '?')"))
			.append(" s (the fuelings same-tenant trigger disabled during the bulk insert, then re-enabled; "
					+ "foreign keys and CHECK constraints enforced; same-tenant rule verified set-based afterwards)\n");
		d.append("- existing fuelings indexes: ")
			.append(rows(owner, "select indexname || ' ' || pg_size_pretty(pg_relation_size(indexname::regclass)) "
					+ "from pg_indexes where tablename = 'fuelings' order by 1")
				.stream()
				.map((r) -> String.valueOf(r[0]))
				.collect(Collectors.joining("; ")))
			.append("\n\n");
		environment.append('\n').append(d);
		write("environment.txt", environment.toString());
	}

	// ------------------------------------------------------------------------------------- phases

	private void runPhase(String url, Connection owner, String phase, Subjects s) throws Exception {
		List<String> v16 = v16Indexes();
		boolean v16Phase = phase.equals("v16") || phase.equals("v16_concurrent");
		if (v16Phase && v16.isEmpty()) {
			log("No V16 migration yet: phase v16 skipped");
			return;
		}
		if (!v16Phase && !CANDIDATES.containsKey(phase)) {
			throw new IllegalArgumentException("Unknown phase " + phase + "; known: " + CANDIDATES.keySet() + ", v16, v16_concurrent");
		}
		log("=== phase " + phase);
		for (Index index : ALL_CANDIDATES) {
			exec(owner, "drop index if exists " + index.name());
		}
		undoV16(owner);
		migrate(url, "11");
		summary.append("## Phase `").append(phase).append("`\n\n");
		List<String> created = new ArrayList<>();
		if (v16Phase) {
			long t = System.nanoTime();
			if (phase.equals("v16")) {
				migrate(url, null);
			}
			else {
				migrateConcurrently(url);
			}
			double seconds = (System.nanoTime() - t) / 1e9;
			summary.append(String.format(Locale.ROOT, "V16 applied by Flyway on the loaded table (%s) in %.1f s.%n%n",
					phase.equals("v16") ? "the committed script: plain CREATE INDEX in one transaction"
							: "CREATE INDEX CONCURRENTLY, executeInTransaction=false, postgresql.transactional.lock=false",
					seconds));
			created.addAll(v16);
		}
		else {
			for (Index index : CANDIDATES.get(phase)) {
				long t = System.nanoTime();
				exec(owner, index.ddl());
				summary.append(String.format(Locale.ROOT, "- built `%s` in %.1f s (plain CREATE INDEX)%n", index.name(),
						(System.nanoTime() - t) / 1e9));
				created.add(index.name());
			}
		}
		exec(owner, "analyze fuelings");
		if (created.isEmpty()) {
			summary.append("Indexes: the V1–V11 indexes only.\n\n");
		}
		else {
			summary.append("\n| Added index | Size | Valid |\n|---|---:|---|\n");
			for (String name : created) {
				summary.append("| `")
					.append(name)
					.append("` | ")
					.append(queryString(owner, "select pg_size_pretty(pg_relation_size('" + name + "'))"))
					.append(" | ")
					.append(queryString(owner, "select indisvalid::text from pg_index where indexrelid = '" + name
							+ "'::regclass"))
					.append(" |\n");
			}
			summary.append('\n');
		}

		summary.append("| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |\n");
		summary.append("|---|---|---:|---:|---:|---:|---:|---:|---|---|\n");
		try (Connection app = DriverManager.getConnection(url, "postgres", "postgres")) {
			app.setAutoCommit(true);
			exec(app, "set role " + "zawed_app");
			exec(app, "set plan_cache_mode = force_custom_plan");
			for (Query q : queries(s)) {
				Result r = run(app, q, false);
				writePlan(phase, q.name(), r.plan());
				summary.append(r.row(q)).append('\n');
				log(String.format(Locale.ROOT, "%-34s rows=%-6d median=%8.2f ms  %s", q.name(), r.rows(), r.median(),
						r.shape()));
			}
			if (phase.equals("baseline") || phase.equals("v16")) {
				summary.append("\nGeneric plans (what PostgreSQL would run if it switched a prepared statement to its "
						+ "generic plan; parameters unknown at plan time):\n\n");
				summary.append("| Query | Scope | Rows | First ms | Median ms | Max ms | EXPLAIN exec ms | Planning ms | Buffers (top) | Plan |\n");
				summary.append("|---|---|---:|---:|---:|---:|---:|---:|---|---|\n");
				for (Query q : queries(s)) {
					if (q.name().equals("company_list_month_old_big") || q.name().equals("ops_list_newest")) {
						Result r = run(app, q, true);
						writePlan(phase, q.name() + "__generic", r.plan());
						summary.append(r.row(q)).append('\n');
					}
				}
			}
		}
		summary.append('\n');
		writeCost(owner, phase);
	}

	// ------------------------------------------------------------------------------------ queries

	enum ScopeType {

		PLATFORM, COMPANY, OPERATOR

	}

	/**
	 * One API statement: its SQL with positional parameters (copied from the repository, named
	 * parameters numbered in order of first use), the parameter types PostgreSQL infers for it, the
	 * scope the API runs it in, and the values.
	 */
	record Query(String name, ScopeType scope, UUID scopeId, String sql, String types, List<String> args) {
	}

	private static final String LIST_SQL = """
			select * from fuelings
			where (cast($1 as uuid) is null or company_id = $1)
			  and (cast($2 as uuid) is null or operator_id = $2)
			  and (cast($3 as uuid) is null or station_id = $3)
			  and (cast($4 as uuid) is null or vehicle_id = $4)
			  and (cast($5 as text) is null or status = $5)
			  and (cast($6 as timestamptz) is null or evaluated_at >= $6)
			  and (cast($7 as timestamptz) is null or evaluated_at < $7)
			  and (cast($8 as uuid) is null or id < $8)
			order by id desc limit $9
			""";
	private static final String LIST_TYPES = "uuid, uuid, uuid, uuid, text, timestamptz, timestamptz, uuid, int";

	private static final String ACTIVITY_SQL = """
			select (evaluated_at at time zone 'Africa/Cairo')::date as day,
			       count(*) filter (where status = 'COMPLETED') as completed,
			       count(*) filter (where status = 'DECLINED') as declined,
			       count(*) filter (where origin = 'OFFLINE') as offline,
			       count(*) filter (where conflicts <> '{}') as conflicted,
			       coalesce(sum(volume_ml) filter (where status = 'COMPLETED'), 0)::bigint as volume,
			       coalesce(sum(amount_piastres) filter (where status = 'COMPLETED'), 0)::bigint as amount
			from fuelings
			where evaluated_at >= $1 and evaluated_at < $2
			  and (cast($3 as uuid) is null or company_id = $3)
			  and (cast($4 as uuid) is null or operator_id = $4)
			  and (cast($5 as uuid) is null or station_id = $5)
			  and (cast($6 as uuid) is null or vehicle_id = $6)
			  and (cast($7 as uuid) is null or device_id = $7)
			  and (cast($8 as uuid) is null or agent_id = $8)
			group by 1
			""";
	private static final String ACTIVITY_ALERTS_SQL = """
			select (created_at at time zone 'Africa/Cairo')::date as day, count(*) as alerts
			from alerts
			where created_at >= $1 and created_at < $2
			  and (cast($3 as uuid) is null or company_id = $3)
			  and (cast($4 as uuid) is null or operator_id = $4)
			  and (cast($5 as uuid) is null or station_id = $5)
			  and (cast($6 as uuid) is null or vehicle_id = $6)
			  and (cast($7 as uuid) is null or device_id = $7)
			  and cast($8 as uuid) is null
			group by 1
			""";
	private static final String ACTIVITY_TYPES = "timestamptz, timestamptz, uuid, uuid, uuid, uuid, uuid, uuid";

	private static final String EXPORT_COUNT_SQL = """
			select count(*) from fuelings
			where evaluated_at >= $1 and evaluated_at < $2
			  and (cast($3 as uuid) is null or company_id = $3)
			  and (cast($4 as uuid) is null or operator_id = $4)
			  and (cast($5 as uuid) is null or station_id = $5)
			  and (cast($6 as uuid) is null or vehicle_id = $6)
			  and (cast($7 as text) is null or status = $7)
			""";
	private static final String EXPORT_ROWS_SQL = """
			select reference, evaluated_at, plate, company_name_ar, company_name_en, station_name_ar, station_name_en,
			       fuel_type, volume_ml, unit_price_piastres, amount_piastres, billable, status, decline_reason, origin,
			       conflicts, agent_worker_id
			from fuelings
			where evaluated_at >= $1 and evaluated_at < $2
			  and (cast($3 as uuid) is null or company_id = $3)
			  and (cast($4 as uuid) is null or operator_id = $4)
			  and (cast($5 as uuid) is null or station_id = $5)
			  and (cast($6 as uuid) is null or vehicle_id = $6)
			  and (cast($7 as text) is null or status = $7)
			order by evaluated_at, reference
			""";
	private static final String EXPORT_TYPES = "timestamptz, timestamptz, uuid, uuid, uuid, uuid, text";

	private static final String USAGE_SQL = """
			select vehicle_id,
			       coalesce(sum(volume_ml) filter (where evaluated_at >= $1), 0)::bigint as day_ml,
			       coalesce(sum(volume_ml) filter (where evaluated_at >= $2), 0)::bigint as week_ml,
			       coalesce(sum(volume_ml) filter (where evaluated_at >= $3), 0)::bigint as month_ml,
			       coalesce(sum(amount_piastres) filter (where evaluated_at >= $1), 0)::bigint as day_p,
			       coalesce(sum(amount_piastres) filter (where evaluated_at >= $2), 0)::bigint as week_p,
			       coalesce(sum(amount_piastres) filter (where evaluated_at >= $3), 0)::bigint as month_p
			from fuelings
			where status = 'COMPLETED' and evaluated_at >= $4
			group by vehicle_id
			""";

	private static final String VOLUME_IN_SQL = "select coalesce(sum(volume_ml), 0)::bigint from fuelings "
			+ "where vehicle_id = $1 and status = 'COMPLETED' and evaluated_at >= $2 and evaluated_at < $3";

	private static final String LATEST_BETWEEN_SQL = """
			select max(evaluated_at) from fuelings
			where vehicle_id = $1 and status = 'COMPLETED' and evaluated_at > $2 and evaluated_at < $3
			""";

	private static final String OFFLINE_SQL = """
			select coalesce(sum(amount_piastres), 0)::bigint from fuelings
			where device_id = $1 and company_id = $2 and origin = 'OFFLINE' and evaluated_at > $3
			""";

	private static List<Query> queries(Subjects s) {
		Instant oldFrom = s.monthStart(s.oldMonth());
		Instant oldTo = s.monthEnd(s.oldMonth());
		Instant lastFrom = s.monthStart(s.lastMonth());
		Instant lastTo = s.monthEnd(s.lastMonth());
		Instant last30 = s.end().minus(Duration.ofDays(30));
		// Activity: one month of Cairo days (from the first to the last day, inclusive).
		Instant actFrom = s.oldMonth().atDay(1).atStartOfDay(CAIRO).toInstant();
		Instant actTo = s.oldMonth().atEndOfMonth().plusDays(1).atStartOfDay(CAIRO).toInstant();
		// The snapshot's periods at the dataset end, in the station's zone (weeks start on Saturday).
		LocalDate today = s.end().atZone(CAIRO).toLocalDate();
		Instant day = today.atStartOfDay(CAIRO).toInstant();
		Instant week = today.with(TemporalAdjusters.previousOrSame(DayOfWeek.SATURDAY)).atStartOfDay(CAIRO).toInstant();
		Instant month = today.withDayOfMonth(1).atStartOfDay(CAIRO).toInstant();
		Instant earliest = Stream.of(day, week, month).min(Comparator.naturalOrder()).orElseThrow();

		List<Query> q = new ArrayList<>();
		// Fuelings.list: Ops, company and station lists, newest first, limit 51 (default page 50 + 1).
		q.add(list("ops_list_newest", ScopeType.PLATFORM, null, null, null, null, null, null));
		q.add(list("ops_list_last30", ScopeType.PLATFORM, null, null, null, null, last30, s.end()));
		q.add(list("ops_list_month_old", ScopeType.PLATFORM, null, null, null, null, oldFrom, oldTo));
		q.add(list("ops_list_company_month_old", ScopeType.PLATFORM, null, s.bigCompany(), null, null, oldFrom, oldTo));
		q.add(list("company_list_newest_big", ScopeType.COMPANY, s.bigCompany(), s.bigCompany(), null, null, null, null));
		q.add(list("company_list_last30_big", ScopeType.COMPANY, s.bigCompany(), s.bigCompany(), null, null, last30,
				s.end()));
		q.add(list("company_list_last_month_big", ScopeType.COMPANY, s.bigCompany(), s.bigCompany(), null, null,
				lastFrom, lastTo));
		q.add(list("company_list_month_old_big", ScopeType.COMPANY, s.bigCompany(), s.bigCompany(), null, null, oldFrom,
				oldTo));
		q.add(list("company_list_month_old_median", ScopeType.COMPANY, s.medianCompany(), s.medianCompany(), null, null,
				oldFrom, oldTo));
		q.add(list("station_list_newest", ScopeType.OPERATOR, s.operator(), null, s.operator(), s.station(), null, null));
		q.add(list("station_list_month_old", ScopeType.OPERATOR, s.operator(), null, s.operator(), s.station(), oldFrom,
				oldTo));
		q.add(list("operator_list_newest", ScopeType.OPERATOR, s.operator(), null, s.operator(), null, null, null));
		q.add(list("operator_list_month_old", ScopeType.OPERATOR, s.operator(), null, s.operator(), null, oldFrom,
				oldTo));
		// Activity.days: one month of day buckets.
		q.add(activity("activity_company_month_big", ACTIVITY_SQL, ScopeType.COMPANY, s.bigCompany(), actFrom, actTo,
				s.bigCompany(), null));
		q.add(activity("activity_company_month_median", ACTIVITY_SQL, ScopeType.COMPANY, s.medianCompany(), actFrom,
				actTo, s.medianCompany(), null));
		q.add(activity("activity_operator_month", ACTIVITY_SQL, ScopeType.OPERATOR, s.operator(), actFrom, actTo, null,
				s.operator()));
		q.add(activity("activity_platform_month", ACTIVITY_SQL, ScopeType.PLATFORM, null, actFrom, actTo, null, null));
		q.add(activity("activity_alerts_company_month", ACTIVITY_ALERTS_SQL, ScopeType.COMPANY, s.bigCompany(), actFrom,
				actTo, s.bigCompany(), null));
		q.add(activity("activity_alerts_platform_month", ACTIVITY_ALERTS_SQL, ScopeType.PLATFORM, null, actFrom, actTo,
				null, null));
		// FuelingExports.csv: the count, then the rows, for a month.
		q.add(export("export_company_month_count", EXPORT_COUNT_SQL, s.bigCompany(), null, lastFrom, lastTo));
		q.add(export("export_company_month_rows", EXPORT_ROWS_SQL, s.bigCompany(), null, lastFrom, lastTo));
		q.add(export("export_operator_month_count", EXPORT_COUNT_SQL, null, s.operator(), lastFrom, lastTo));
		q.add(export("export_operator_month_rows", EXPORT_ROWS_SQL, null, s.operator(), lastFrom, lastTo));
		// FuelingUsageReader.usageSince: the POS snapshot's limit usage, platform-wide, since the period starts.
		q.add(new Query("usage_snapshot_since_period_starts", ScopeType.PLATFORM, null, USAGE_SQL,
				"timestamptz, timestamptz, timestamptz, timestamptz", List.of(ts(day), ts(week), ts(month), ts(earliest))));
		// Fuelings.volumeIn / latestCompletedBetween / offlineAmountSince: the engine, under the wallet lock.
		q.add(new Query("engine_vehicle_volume_in_month", ScopeType.PLATFORM, null, VOLUME_IN_SQL,
				"uuid, timestamptz, timestamptz", List.of(uuid(s.vehicle()), ts(month), ts(s.monthEnd(YearMonth.from(today))))));
		q.add(new Query("engine_latest_completed_between", ScopeType.PLATFORM, null, LATEST_BETWEEN_SQL,
				"uuid, timestamptz, timestamptz",
				List.of(uuid(s.vehicle()), ts(s.end().minus(Duration.ofHours(3))), ts(s.end().plus(Duration.ofHours(3))))));
		q.add(new Query("engine_offline_amount_since_24h", ScopeType.PLATFORM, null, OFFLINE_SQL,
				"uuid, uuid, timestamptz",
				List.of(uuid(s.offlineDevice()), uuid(s.offlineCompany()), ts(s.end().minus(Duration.ofHours(24))))));
		// NOT the API's SQL: a candidate rewrite of the list for a date range, newest first by time
		// instead of by id, measured only to size a follow-up change (indexes alone do not fix the
		// ranged lists; see the report).
		String byTime = LIST_SQL.replace("order by id desc", "order by evaluated_at desc, id desc");
		for (Query list : List.of(q.get(2), q.get(3), q.get(7), q.get(12))) {
			q.add(new Query("proposal_" + list.name() + "_by_time", list.scope(), list.scopeId(), byTime, list.types(),
					list.args()));
		}
		return q;
	}

	private static Query list(String name, ScopeType scope, UUID scopeId, UUID company, UUID operator, UUID station,
			Instant from, Instant to) {
		return new Query(name, scope, scopeId, LIST_SQL, LIST_TYPES,
				List.of(uuid(company), uuid(operator), uuid(station), "null", "null", ts(from), ts(to), "null", "51"));
	}

	private static Query activity(String name, String sql, ScopeType scope, UUID scopeId, Instant from, Instant to,
			UUID company, UUID operator) {
		return new Query(name, scope, scopeId, sql, ACTIVITY_TYPES,
				List.of(ts(from), ts(to), uuid(company), uuid(operator), "null", "null", "null", "null"));
	}

	private static Query export(String name, String sql, UUID company, UUID operator, Instant from, Instant to) {
		ScopeType scope = company != null ? ScopeType.COMPANY : ScopeType.OPERATOR;
		UUID scopeId = company != null ? company : operator;
		return new Query(name, scope, scopeId, sql, EXPORT_TYPES,
				List.of(ts(from), ts(to), uuid(company), uuid(operator), "null", "null", "null"));
	}

	private static String uuid(UUID id) {
		return id == null ? "null" : "'" + id + "'";
	}

	private static String ts(Instant at) {
		return at == null ? "null" : "'" + at + "'";
	}

	// ---------------------------------------------------------------------------------- execution

	record Result(long rows, double first, double median, double max, String plan, String shape) {

		double explainMs() {
			return number(plan, "Execution Time: ([0-9.]+) ms");
		}

		double planningMs() {
			return number(plan, "Planning Time: ([0-9.]+) ms");
		}

		String buffers() {
			Matcher m = Pattern.compile("Buffers: ([^\\n]+)").matcher(plan);
			return m.find() ? m.group(1).trim() : "";
		}

		String row(Query q) {
			return String.format(Locale.ROOT, "| %s | %s | %d | %.2f | %.2f | %.2f | %.2f | %.2f | %s | %s |", q.name(),
					q.scope(), rows, first, median, max, explainMs(), planningMs(), buffers(), shape);
		}

		private static double number(String text, String regex) {
			Matcher m = Pattern.compile(regex).matcher(text);
			return m.find() ? Double.parseDouble(m.group(1)) : Double.NaN;
		}

	}

	/** First execution, EXPLAIN (ANALYZE, BUFFERS), then RUNS timed executions, all in the query's scope. */
	private static Result run(Connection app, Query q, boolean generic) throws SQLException {
		String args = String.join(", ", q.args());
		// A fresh name each time: DEALLOCATE ALL would also drop the driver's own server-side statements.
		String name = "perf_q" + STATEMENTS.incrementAndGet();
		String execute = "execute " + name + "(" + args + ")";
		exec(app, "prepare " + name + " (" + q.types() + ") as " + q.sql());
		try {
			if (generic) {
				exec(app, "set plan_cache_mode = force_generic_plan");
			}
			long[] rows = new long[1];
			double first = inScope(app, q, () -> rows[0] = consume(app, execute));
			StringBuilder plan = new StringBuilder();
			inScope(app, q, () -> {
				try (Statement st = app.createStatement();
						ResultSet rs = st.executeQuery("explain (analyze, buffers, settings) " + execute)) {
					while (rs.next()) {
						plan.append(rs.getString(1)).append('\n');
					}
				}
				return 0;
			});
			double[] times = new double[RUNS];
			for (int i = 0; i < RUNS; i++) {
				times[i] = inScope(app, q, () -> consume(app, execute));
			}
			Arrays.sort(times);
			return new Result(rows[0], first, times[RUNS / 2], times[RUNS - 1], plan.toString(), shape(plan.toString()));
		}
		finally {
			exec(app, "set plan_cache_mode = force_custom_plan");
			exec(app, "deallocate " + name);
		}
	}

	private static final java.util.concurrent.atomic.AtomicInteger STATEMENTS = new java.util.concurrent.atomic.AtomicInteger();

	@FunctionalInterface
	interface SqlWork {

		long run() throws SQLException;

	}

	/**
	 * One transaction with the scope set the way {@code ScopedTransactionManager} sets it (three
	 * transaction-local settings, and the role checked), returning the elapsed milliseconds of the work.
	 */
	private static double inScope(Connection app, Query q, SqlWork work) throws SQLException {
		app.setAutoCommit(false);
		try {
			try (PreparedStatement st = app.prepareStatement("select set_config('app.scope_type', ?, true), "
					+ "set_config('app.scope_id', ?, true), set_config('app.user_id', ?, true), current_user")) {
				st.setString(1, q.scope().name());
				st.setString(2, q.scopeId() == null ? "" : q.scopeId().toString());
				st.setString(3, "");
				try (ResultSet rs = st.executeQuery()) {
					rs.next();
					assertThat(rs.getString(4)).isEqualTo("zawed_app");
				}
			}
			long t = System.nanoTime();
			work.run();
			double ms = (System.nanoTime() - t) / 1e6;
			app.commit();
			return ms;
		}
		catch (SQLException ex) {
			app.rollback();
			throw ex;
		}
		finally {
			app.setAutoCommit(true);
		}
	}

	private static long consume(Connection app, String sql) throws SQLException {
		long n = 0;
		try (Statement st = app.createStatement()) {
			st.setFetchSize(0);
			try (ResultSet rs = st.executeQuery(sql)) {
				int columns = rs.getMetaData().getColumnCount();
				while (rs.next()) {
					for (int c = 1; c <= columns; c++) {
						rs.getObject(c);
					}
					n++;
				}
			}
		}
		return n;
	}

	private static final Pattern NODE = Pattern.compile(
			"(Parallel Seq Scan on \\w+|Seq Scan on \\w+|Parallel Index Only Scan(?: Backward)? using \\w+|Index Only Scan(?: Backward)? using \\w+|"
					+ "Parallel Index Scan(?: Backward)? using \\w+|Index Scan(?: Backward)? using \\w+|Bitmap Index Scan on \\w+|"
					+ "Bitmap Heap Scan on \\w+|Sort Method: [\\w-]+(?: \\w+)?|HashAggregate|GroupAggregate|Gather(?: Merge)?)");

	/** The plan's access paths and sorts, in order, without repeats: enough to compare phases at a glance. */
	private static String shape(String plan) {
		Set<String> parts = new LinkedHashSet<>();
		Matcher m = NODE.matcher(plan);
		while (m.find()) {
			parts.add(m.group(1));
		}
		Matcher removed = Pattern.compile("Rows Removed by Filter: (\\d+)").matcher(plan);
		long filtered = 0;
		while (removed.find()) {
			filtered += Long.parseLong(removed.group(1));
		}
		return String.join(" → ", parts) + (filtered > 0 ? String.format(Locale.ROOT, " (filter removed %,d)", filtered) : "");
	}

	// --------------------------------------------------------------------------------- write cost

	/**
	 * Rows to insert for the write-cost measurement: recent fuelings, as new ones would be (the right
	 * edge of every time index), at random companies, vehicles and stations.
	 */
	private static void prepareWriteSamples(Connection owner, Subjects s) throws SQLException {
		exec(owner, "drop table if exists perf.sample");
		String end = "('" + s.end() + "'::timestamptz + interval '1 hour')";
		exec(owner, "create table perf.sample as select row_number() over () as n, x.* from ("
				+ generator(FUELINGS + 1L, FUELINGS + 20_000L, end) + ") x");
		exec(owner, "create unique index on perf.sample (n)");
	}

	private void writeCost(Connection owner, String phase) throws SQLException {
		double[] single = new double[3];
		double[] bulk = new double[3];
		for (int i = 0; i < 3; i++) {
			owner.setAutoCommit(false);
			try {
				long t = System.nanoTime();
				try (PreparedStatement st = owner
					.prepareStatement("insert into fuelings (" + COLUMNS + ") select " + COLUMNS + " from perf.sample where n = ?")) {
					for (int n = 1; n <= 2_000; n++) {
						st.setLong(1, n);
						st.executeUpdate();
					}
				}
				single[i] = (System.nanoTime() - t) / 1e6 / 2_000;
			}
			finally {
				owner.rollback();
				owner.setAutoCommit(true);
			}
			owner.setAutoCommit(false);
			try {
				long t = System.nanoTime();
				exec(owner, "insert into fuelings (" + COLUMNS + ") select " + COLUMNS + " from perf.sample");
				bulk[i] = (System.nanoTime() - t) / 1e6;
			}
			finally {
				owner.rollback();
				owner.setAutoCommit(true);
			}
		}
		Arrays.sort(single);
		Arrays.sort(bulk);
		String line = String.format(Locale.ROOT,
				"Write cost (median of 3, rolled back; same-tenant trigger on): single-row insert %.3f ms/row "
						+ "(2,000 inserts in one transaction); bulk insert of 20,000 rows %.0f ms.%n%n",
				single[1], bulk[1]);
		summary.append(line);
		log(line.trim());
	}

	// ------------------------------------------------------------------------------------ helpers

	@FunctionalInterface
	interface Step {

		void run() throws Exception;

	}

	private static void timed(String what, Step step) throws Exception {
		long t = System.nanoTime();
		step.run();
		log(String.format(Locale.ROOT, "%s loaded in %.1f s", what, (System.nanoTime() - t) / 1e9));
	}

	private static void exec(Connection c, String sql) throws SQLException {
		try (Statement st = c.createStatement()) {
			st.execute(sql);
		}
	}

	private static boolean exists(Connection c, String sql) throws SQLException {
		try (Statement st = c.createStatement(); ResultSet rs = st.executeQuery(sql)) {
			return rs.next();
		}
	}

	private static String queryString(Connection c, String sql) throws SQLException {
		try (Statement st = c.createStatement(); ResultSet rs = st.executeQuery(sql)) {
			return rs.next() ? rs.getString(1) : null;
		}
	}

	private static long queryLong(Connection c, String sql, Object... params) throws SQLException {
		return ((Number) rows(c, sql, params).getFirst()[0]).longValue();
	}

	private static List<Object[]> rows(Connection c, String sql, Object... params) throws SQLException {
		try (PreparedStatement st = c.prepareStatement(sql)) {
			for (int i = 0; i < params.length; i++) {
				st.setObject(i + 1, params[i]);
			}
			try (ResultSet rs = st.executeQuery()) {
				List<Object[]> out = new ArrayList<>();
				int columns = rs.getMetaData().getColumnCount();
				while (rs.next()) {
					Object[] row = new Object[columns];
					for (int i = 0; i < columns; i++) {
						row[i] = rs.getObject(i + 1);
					}
					out.add(row);
				}
				return out;
			}
		}
	}

	private static void writePlan(String phase, String query, String plan) throws IOException {
		Path dir = OUT.resolve("plans").resolve(phase);
		Files.createDirectories(dir);
		Files.writeString(dir.resolve(query + ".txt"), plan, StandardCharsets.UTF_8);
	}

	private static void write(String file, String text) {
		try {
			Files.writeString(OUT.resolve(file), text, StandardCharsets.UTF_8);
		}
		catch (IOException ex) {
			throw new UncheckedIOException(ex);
		}
	}

	private static void deleteRecursively(Path dir) throws IOException {
		if (!Files.exists(dir)) {
			return;
		}
		try (Stream<Path> paths = Files.walk(dir)) {
			for (Path p : paths.sorted(Comparator.reverseOrder()).toList()) {
				Files.delete(p);
			}
		}
	}

	private static void log(String message) {
		System.out.println("[perf] " + Instant.now() + " " + message);
	}

}
