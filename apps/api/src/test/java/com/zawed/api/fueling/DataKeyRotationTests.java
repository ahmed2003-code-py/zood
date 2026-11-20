package com.zawed.api.fueling;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Statement;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Base64;
import java.util.HashMap;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.Collectors;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import javax.sql.DataSource;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.junit.jupiter.api.extension.ExtendWith;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.system.CapturedOutput;
import org.springframework.boot.test.system.OutputCaptureExtension;
import org.springframework.context.ApplicationContext;
import org.springframework.security.crypto.argon2.Argon2PasswordEncoder;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;
import org.springframework.test.context.TestPropertySource;

import com.zawed.api.platform.crypto.DataEncryptor;
import com.zawed.api.platform.crypto.DataKeyRotationAccess;
import com.zawed.api.platform.crypto.DataKeyRotationAccess.Result;
import com.zawed.api.platform.crypto.EncryptedColumn;
import com.zawed.api.platform.ratelimit.InMemoryRateLimiter;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;
import com.zawed.api.support.Concurrently;
import com.zawed.api.support.TestDatabase;

/**
 * Data-key rotation end to end (ADR 0027, B7 W9): the application runs as it does in the middle of
 * a rotation, with key 2 current, key 1 kept to read, and the lookup key pinned to key 1.
 *
 * <p>Values are first written through the real API, then turned back into key-1 values, which is
 * what a database written before the rotation holds. The job then moves them to key 2, and every
 * place that reads them is used again: TOTP sign-in and enrolment, the offline snapshot's tag
 * digest, a fueling by tag, the station's bank account.
 *
 * <p>This class has a database of its own. The job re-encrypts every value it can see, and the
 * other test classes run with key 1 only, so they must never meet a key-2 value.
 */
@TestPropertySource(properties = { "zawed.security.data-key=" + DataKeyRotationTests.KEY_2,
		"zawed.security.data-key-id=2", "zawed.security.data-keys-previous=1:" + DataKeyRotationTests.KEY_1,
		"zawed.security.lookup-key=" + DataKeyRotationTests.KEY_1, "zawed.data-key-rotation.enabled=false" })
@ExtendWith(OutputCaptureExtension.class)
class DataKeyRotationTests extends PosTestSupport {

	/** Key 1: the committed development key, so this is the key every other test class writes with. */
	static final String KEY_1 = "bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=";

	static final String KEY_2 = "bmV3LWRhdGEta2V5LWZvci1yb3RhdGlvbi10ZXN0cyE=";

	private static final String MFA = "users.mfa_secret_encrypted";
	private static final String PENDING_MFA = "mfa_challenges.pending_secret_encrypted";
	private static final String TAG_UID = "rfid_tags.uid_encrypted";
	private static final String BANK_ACCOUNT = "station_bank_accounts.account_encrypted";

	private static final Argon2PasswordEncoder ARGON2 = Argon2PasswordEncoder.defaultsForSpringSecurity_v5_8();
	private static final SecureRandom RANDOM = new SecureRandom();
	private static final AtomicInteger SEQUENCE = new AtomicInteger();

	/** What a deployment that never rotated writes with. */
	private static final DataEncryptor KEY_1_ONLY = DataKeyRotationAccess.encryptor(1,
			Map.of(1, Base64.getDecoder().decode(KEY_1)));

	/** What the deployment that retires key 1 reads with. */
	private static final DataEncryptor KEY_2_ONLY = DataKeyRotationAccess.encryptor(2,
			Map.of(2, Base64.getDecoder().decode(KEY_2)));

	/** A key newer than the deployment's current one: the database never accepts a value under it. */
	private static final DataEncryptor KEY_3 = DataKeyRotationAccess.encryptor(3,
			Map.of(3, Base64.getDecoder().decode("dGhpcmQtZGF0YS1rZXktZm9yLXJvdGF0aW9uLXRlc3Q=")));

	private static String databaseUrl;

	@Autowired
	DataEncryptor dataEncryptor;

	@Autowired
	List<EncryptedColumn> encryptedColumns;

	@Autowired
	ApplicationContext context;

	@Autowired
	DataSource dataSource;

	@Autowired
	InMemoryRateLimiter rateLimits;

	@DynamicPropertySource
	static void ownDatabase(DynamicPropertyRegistry registry) {
		registry.add(OWN_DATABASE_URL, DataKeyRotationTests::databaseUrl);
	}

	// ------------------------------------------------------------------------------------- tests

	@Test
	void everyEncryptedColumnIsMovedToTheNewKeyAndStillWorksWhereItIsUsed() throws Exception {
		assertRunsOnItsOwnDatabase();

		// Arrange through the API: a pump, a funded company with a tag, a user with MFA, an enrolment in
		// progress, a station with a verified bank account and a change waiting.
		Pump pump = pump();
		price(pump.stationId(), "GAS_92", PRICE);
		String company = company().toString();
		credit(company, 1_000_000);
		Car car = car(company);
		SeededUser mfaUser = seed(Role.OPS);
		SeededUser enrolling = seed(Role.OPS);
		exec("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = ?", enrolling.id());
		HttpResponse<String> login = post("/api/v1/auth/login", "{\"email\":\"" + enrolling.email() + "\",\"password\":\""
				+ PASSWORD + "\"}");
		assertThat(JsonPath.<String>read(login.body(), "$.status")).isEqualTo("MFA_ENROLLMENT_REQUIRED");
		String challenge = JsonPath.read(login.body(), "$.challengeToken");
		String shownSecret = enrolmentSecret(challenge);
		SignedIn secondOps = signIn(seed(Role.OPS));
		String change = requestBankChange(pump.stationId(), "EG38 0019 0005 0000 0000 2631 80002");
		assertThat(authed("POST", "/api/v1/ops/bank-account-changes/" + change + "/approve", secondOps.accessToken(), null)
			.statusCode()).isEqualTo(200);
		String waiting = requestBankChange(pump.stationId(), "1234567890123");
		rateLimits.reset();

		// The database as a deployment before the rotation left it: every value under key 1.
		int downgraded = downgradeEverything();
		Map<String, Map<UUID, byte[]>> plaintexts = new LinkedHashMap<>();
		for (EncryptedColumn column : encryptedColumns) {
			assertThat(keyIds(column)).as(column.name()).containsOnly(1);
			plaintexts.put(column.name(), plaintexts(column, dataEncryptor));
		}
		assertThat(plaintexts.get(MFA)).containsKey(mfaUser.id());
		assertThat(plaintexts.get(PENDING_MFA)).isNotEmpty();
		assertThat(plaintexts.get(BANK_ACCOUNT)).containsKeys(UUID.fromString(change), UUID.fromString(waiting));
		String lookupBefore = queryOne("select uid_lookup from rfid_tags where vehicle_id = ?",
				UUID.fromString(car.vehicleId()));

		// Mid-rotation, before the job: old values are read with the previous key where they are used.
		signIn(mfaUser);
		assertThat(enrolmentSecret(challenge)).isEqualTo(shownSecret);
		assertThat(snapshotDigests(pump)).contains(digestOf(pump, car));

		// Taken after those reads, which legitimately write (a sign-in records its TOTP step).
		Map<String, Map<UUID, Object>> rowsBefore = new LinkedHashMap<>();
		for (EncryptedColumn column : encryptedColumns) {
			rowsBefore.put(column.name(), rowsWithoutCiphertext(column));
		}

		Result run = DataKeyRotationAccess.run(context);

		assertThat(run.failed()).isZero();
		assertThat(run.reencrypted()).isEqualTo(downgraded);
		assertThat(run.remaining()).isZero();
		for (EncryptedColumn column : encryptedColumns) {
			assertThat(keyIds(column)).as(column.name()).containsOnly(2);
			// Readable with key 2 alone, the same plaintext, bound to the same row: key 1 can be retired.
			assertThat(plaintexts(column, KEY_2_ONLY)).as(column.name())
				.usingRecursiveComparison()
				.isEqualTo(plaintexts.get(column.name()));
			// Nothing else in any row changed: not updated_at, not who changed it, not the status.
			assertThat(rowsWithoutCiphertext(column)).as(column.name()).isEqualTo(rowsBefore.get(column.name()));
		}
		assertThat((String) queryOne("select uid_lookup from rfid_tags where vehicle_id = ?",
				UUID.fromString(car.vehicleId()))).isEqualTo(lookupBefore);

		// After the job, every reader still works.
		signIn(mfaUser);
		byte[] enrolmentKey = base32Decode(shownSecret);
		HttpResponse<String> confirmed = post("/api/v1/auth/mfa/enroll/confirm", "{\"challengeToken\":\"" + challenge
				+ "\",\"code\":\"" + totp(enrolmentKey, Instant.now().getEpochSecond() / 30) + "\"}");
		assertThat(confirmed.statusCode()).as(confirmed.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(confirmed.body(), "$.status")).isEqualTo("AUTHENTICATED");
		assertThat(DataEncryptor.keyIdOf(queryOne("select mfa_secret_encrypted from users where id = ?", enrolling.id())))
			.as("a new write uses the current key")
			.isEqualTo(2);
		assertThat(snapshotDigests(pump)).contains(digestOf(pump, car));
		HttpResponse<String> fueled = fuel(pump, car, 10_000);
		assertThat(fueled.statusCode()).as("the tag is still found by its lookup hash: %s", fueled.body()).isEqualTo(201);
		SignedIn manager = signIn(seed(Role.STATION_MANAGER,
				new Scope(ScopeType.OPERATOR, UUID.fromString(pump.operatorId())), false));
		HttpResponse<String> bank = authed("GET", "/api/v1/station/stations/" + pump.stationId() + "/bank-account",
				manager.accessToken(), null);
		assertThat(bank.statusCode()).as(bank.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(bank.body(), "$.active.accountLast4")).isEqualTo("0002");

		// Running again finds nothing to do.
		Result again = DataKeyRotationAccess.run(context);
		assertThat(again.reencrypted()).isZero();
		assertThat(again.remaining()).isZero();
	}

	@Test
	void theDatabaseAllowsARekeyOfATagOrBankAccountAndNothingElse() throws Exception {
		assertRunsOnItsOwnDatabase();
		String company = company().toString();
		Car car = car(company);
		String operator = operator();
		String station = station(operator);
		String accountId = requestBankChange(station, "EG38 0019 0005 0000 0000 2631 80002");
		UUID tag = queryOne("select id from rfid_tags where vehicle_id = ?", UUID.fromString(car.vehicleId()));
		UUID account = UUID.fromString(accountId);
		EncryptedColumn tags = column(TAG_UID);
		EncryptedColumn accounts = column(BANK_ACCOUNT);
		downgrade(tags, List.of(tag));
		downgrade(accounts, List.of(account));
		byte[] oldTag = queryOne("select uid_encrypted from rfid_tags where id = ?", tag);
		byte[] oldAccount = queryOne("select account_encrypted from station_bank_accounts where id = ?", account);
		byte[] uid = dataEncryptor.decrypt(oldTag, tags.binding().apply(tag));
		byte[] number = dataEncryptor.decrypt(oldAccount, accounts.binding().apply(account));

		// As the application's own role, in the platform scope the job uses.
		try (Connection app = connect(); Statement setup = app.createStatement()) {
			setup.execute("set role zawed_app");
			setup.execute("select set_config('app.scope_type', 'PLATFORM', false)");
			String tagUpdate = "update rfid_tags set uid_encrypted = ? where id = ?";
			String frozen = "its UID, vehicle and company never change";

			assertThat(refusal(app, tagUpdate, KEY_1_ONLY.encrypt(uid, tags.binding().apply(tag)), tag))
				.as("rewritten under the same key").contains(frozen);
			assertThat(refusal(app, tagUpdate, KEY_3.encrypt(uid, tags.binding().apply(tag)), tag))
				.as("under a key newer than the deployment's current key").contains(frozen);
			assertThat(refusal(app, tagUpdate, dataEncryptor.encrypt(concat(uid, "00"), tags.binding().apply(tag)), tag))
				.as("another value of another length").contains(frozen);
			assertThat(refusal(app, "update rfid_tags set uid_encrypted = ?, status_reason = 'x' where id = ?",
					dataEncryptor.reencrypt(oldTag, tags.binding().apply(tag)), tag))
				.as("a rekey that changes something else too").contains(frozen);
			assertThat(update(app, tagUpdate, dataEncryptor.reencrypt(oldTag, tags.binding().apply(tag)), tag)).isEqualTo(1);
			assertThat(refusal(app, tagUpdate, KEY_1_ONLY.encrypt(uid, tags.binding().apply(tag)), tag))
				.as("back under the older key").contains(frozen);
			assertThat(refusal(app, tagUpdate, dataEncryptor.encrypt(uid, tags.binding().apply(tag)), tag))
				.as("once under the current key, frozen again").contains(frozen);
			// The swap of the review finding: same length, another UID, under the current key. Once the row
			// is under the current key (always, outside a rotation) it is refused.
			assertThat(refusal(app, tagUpdate, dataEncryptor.encrypt(otherOfSameLength(uid), tags.binding().apply(tag)),
					tag))
				.as("another UID of the same length under the current key").contains(frozen);
			assertThat(refusal(app, tagUpdate, KEY_3.encrypt(otherOfSameLength(uid), tags.binding().apply(tag)), tag))
				.as("another UID of the same length under a newer key").contains(frozen);

			String accountUpdate = "update station_bank_accounts set account_encrypted = ? where id = ?";
			String details = "cannot change its station, details or requester";
			assertThat(refusal(app, accountUpdate, KEY_1_ONLY.encrypt(number, accounts.binding().apply(account)), account))
				.as("rewritten under the same key").contains(details);
			assertThat(refusal(app, accountUpdate, KEY_3.encrypt(number, accounts.binding().apply(account)), account))
				.as("under a key newer than the deployment's current key").contains(details);
			// updated_at is a column the role may write; with a rekey it is refused, so the job cannot touch it.
			assertThat(refusal(app, "update station_bank_accounts set account_encrypted = ?, updated_at = now() where id = ?",
					dataEncryptor.reencrypt(oldAccount, accounts.binding().apply(account)), account))
				.as("a rekey that changes something else too").contains(details);
			assertThat(refusal(app, "update station_bank_accounts set bank_name = 'Other' where id = ?", account))
				.as("the account's details are still not the role's to write").contains("permission denied");
			assertThat(update(app, accountUpdate, dataEncryptor.reencrypt(oldAccount, accounts.binding().apply(account)),
					account)).isEqualTo(1);
			assertThat(refusal(app, accountUpdate, KEY_1_ONLY.encrypt(number, accounts.binding().apply(account)), account))
				.as("back under the older key").contains(details);
			assertThat(refusal(app, accountUpdate,
					dataEncryptor.encrypt(otherOfSameLength(number), accounts.binding().apply(account)), account))
				.as("another number of the same length under the current key").contains(details);
		}
		assertThat(DataEncryptor.keyIdOf(queryOne("select uid_encrypted from rfid_tags where id = ?", tag))).isEqualTo(2);
		assertThat((String) queryOne("select status from station_bank_accounts where id = ?", account)).isEqualTo("PENDING");
		// The database audits the one change it let through, with key ids only.
		assertThat(rekeyAudits(account)).containsExactly("key 1 -> 2, station " + station + ", actor none");
	}

	@Test
	void aSameLengthSwapInsideARotationWindowNeverReachesADeviceAndIsAudited(CapturedOutput output) throws Exception {
		assertRunsOnItsOwnDatabase();
		Pump pump = pump();
		Car car = car(company().toString());
		String accountId = requestBankChange(pump.stationId(), "EG38 0019 0005 0000 0000 2631 80002");
		UUID tag = queryOne("select id from rfid_tags where vehicle_id = ?", UUID.fromString(car.vehicleId()));
		UUID account = UUID.fromString(accountId);
		EncryptedColumn tags = column(TAG_UID);
		EncryptedColumn accounts = column(BANK_ACCOUNT);
		byte[] original = queryOne("select uid_encrypted from rfid_tags where id = ?", tag);
		downgrade(tags, List.of(tag));
		downgrade(accounts, List.of(account));
		byte[] uid = dataEncryptor.decrypt(queryOne("select uid_encrypted from rfid_tags where id = ?", tag),
				tags.binding().apply(tag));
		byte[] number = dataEncryptor.decrypt(
				queryOne("select account_encrypted from station_bank_accounts where id = ?", account),
				accounts.binding().apply(account));
		String swappedUid = new String(otherOfSameLength(uid), StandardCharsets.US_ASCII);
		assertThat(snapshotDigests(pump)).contains(digestOf(pump, car));

		try {
			// What the database cannot tell from a re-encryption (ADR 0027, residual risk): someone with the
			// current data key and a runtime session, over rows still under the old key.
			try (Connection app = connect(); Statement setup = app.createStatement()) {
				setup.execute("set role zawed_app");
				setup.execute("select set_config('app.scope_type', 'PLATFORM', false)");
				assertThat(update(app, "update rfid_tags set uid_encrypted = ? where id = ?",
						dataEncryptor.encrypt(swappedUid.getBytes(StandardCharsets.US_ASCII), tags.binding().apply(tag)), tag))
					.isEqualTo(1);
				assertThat(update(app, "update station_bank_accounts set account_encrypted = ? where id = ?",
						dataEncryptor.encrypt(otherOfSameLength(number), accounts.binding().apply(account)), account))
					.isEqualTo(1);
			}

			// The swapped UID does not match the tag's lookup hash, which no data key makes: no device gets
			// it, nor the tag's real digest from a value that is no longer its UID.
			String tagKey = tagKey(pump);
			List<String> digests = snapshotDigests(pump);
			assertThat(digests).doesNotContain(SnapshotTests.digest(tagKey, swappedUid))
				.doesNotContain(SnapshotTests.digest(tagKey, car.tagUid()));
			assertThat(output).contains("do not match their lookup hash [" + tag + "]")
				.doesNotContain(swappedUid)
				.doesNotContain(car.tagUid());
			// Online, the tag is still found by its lookup hash, never by the ciphertext.
			assertThat((UUID) queryOne("select id from rfid_tags where uid_lookup = ?",
					lookupHash(car.tagUid()))).isEqualTo(tag);
			// The bank account change cannot be made without its trace in the append-only log.
			assertThat(rekeyAudits(account)).hasSize(1);
		}
		finally {
			try (Connection owner = connect(); Statement replica = owner.createStatement()) {
				replica.execute("set session_replication_role = replica");
				update(owner, "update rfid_tags set uid_encrypted = ? where id = ?", original, tag);
				update(owner, "update station_bank_accounts set account_encrypted = ? where id = ?",
						dataEncryptor.encrypt(number, accounts.binding().apply(account)), account);
			}
		}
		assertThat(snapshotDigests(pump)).contains(digestOf(pump, car));
	}

	@Test
	void oneUnreadableTagIsLeftOutOfTheSnapshotAndEveryOtherTagStillArrives(CapturedOutput output) throws Exception {
		assertRunsOnItsOwnDatabase();
		Pump pump = pump();
		String company = company().toString();
		Car broken = car(company);
		Car fine = car(company);
		UUID tag = queryOne("select id from rfid_tags where vehicle_id = ?", UUID.fromString(broken.vehicleId()));
		byte[] original = queryOne("select uid_encrypted from rfid_tags where id = ?", tag);
		byte[] unknown = original.clone();
		unknown[0] = 9; // a key this instance does not hold
		try {
			try (Connection owner = connect(); Statement replica = owner.createStatement()) {
				replica.execute("set session_replication_role = replica");
				update(owner, "update rfid_tags set uid_encrypted = ? where id = ?", unknown, tag);
			}

			List<String> digests = snapshotDigests(pump);

			assertThat(digests).contains(digestOf(pump, fine)).doesNotContain(digestOf(pump, broken));
			assertThat(output).contains("Offline snapshot: 1 tags left out; 1 could not be decrypted [" + tag + "]")
				.doesNotContain(HexFormat.of().formatHex(unknown))
				.doesNotContain(Base64.getEncoder().encodeToString(unknown))
				.doesNotContain(broken.tagUid());
		}
		finally {
			try (Connection owner = connect(); Statement replica = owner.createStatement()) {
				replica.execute("set session_replication_role = replica");
				update(owner, "update rfid_tags set uid_encrypted = ? where id = ?", original, tag);
			}
		}
		assertThat(snapshotDigests(pump)).contains(digestOf(pump, broken));
	}

	@Test
	@Timeout(120)
	void aTagAFuelingReferencesIsStillReencryptedAndALockedRowIsTakenOnTheNextRun() throws Exception {
		assertRunsOnItsOwnDatabase();
		String company = company().toString();
		UUID referenced = queryOne("select id from rfid_tags where vehicle_id = ?", UUID.fromString(car(company).vehicleId()));
		UUID locked = queryOne("select id from rfid_tags where vehicle_id = ?", UUID.fromString(car(company).vehicleId()));
		downgrade(column(TAG_UID), List.of(referenced, locked));

		try (Connection other = connect()) {
			other.setAutoCommit(false);
			// What a fueling's foreign key check holds on its tag until the fueling commits, and what an
			// application transaction that changes the tag holds.
			queryOne(other, "select id from rfid_tags where id = ? for key share", referenced);
			queryOne(other, "select id from rfid_tags where id = ? for update", locked);

			DataKeyRotationAccess.run(context);

			assertThat(DataEncryptor.keyIdOf(queryOne("select uid_encrypted from rfid_tags where id = ?", referenced)))
				.as("a key share lock does not stop the job").isEqualTo(2);
			assertThat(DataEncryptor.keyIdOf(queryOne("select uid_encrypted from rfid_tags where id = ?", locked)))
				.as("a row another transaction holds is skipped, not waited for").isEqualTo(1);
			other.rollback();
		}

		DataKeyRotationAccess.run(context);

		assertThat(DataEncryptor.keyIdOf(queryOne("select uid_encrypted from rfid_tags where id = ?", locked)))
			.as("and taken on the next run").isEqualTo(2);
	}

	@Test
	void theKeyStateIsTheOwnersAndAnInstanceWithAnotherCurrentKeyDoesNothing(CapturedOutput output) throws Exception {
		assertRunsOnItsOwnDatabase();
		assertThat((Integer) queryOne("select current_key_id::integer from data_key_state")).isEqualTo(2);
		assertThat((String) queryOne("select lookup_key_fingerprint from data_key_state"))
			.as("an HMAC under the lookup key (key 1), never the key").isEqualTo(fingerprintOf(KEY_1))
			.doesNotContain(KEY_1);
		try (Connection app = connect(); Statement sql = app.createStatement()) {
			sql.execute("set role zawed_app");
			sql.execute("select set_config('app.scope_type', 'PLATFORM', false)");
			assertThat(refusal(app, "update data_key_state set current_key_id = 3")).contains("permission denied");
			assertThat(refusal(app, "delete from data_key_state")).contains("permission denied");
			assertThat((Integer) queryOne(app, "select data_key_current_id()")).isEqualTo(2);
		}

		Map<UUID, byte[]> users = oldUsers(1);
		exec("update data_key_state set current_key_id = 3");
		try {
			Result run = DataKeyRotationAccess.run(context);

			assertThat(run.reencrypted()).isZero();
			assertThat(keyIdsOf(users.keySet())).containsOnly(1);
			assertThat(output).contains("this instance's current key 2 is not the deployment's current key 3");
		}
		finally {
			exec("update data_key_state set current_key_id = 2");
		}
		assertThat(DataKeyRotationAccess.run(context).reencrypted()).isPositive();
		assertSecrets(users);
	}

	@Test
	void aRunIsResumableAndTwoRunsAtOnceNeverReencryptAValueTwice() throws Exception {
		assertRunsOnItsOwnDatabase();
		DataKeyRotationAccess.run(context); // whatever earlier tests left
		Map<UUID, byte[]> first = oldUsers(25);

		Result partial = DataKeyRotationAccess.run(context, 10, 1);
		assertThat(partial.reencrypted()).isEqualTo(10);
		assertThat(partial.remainingByColumn().get(MFA)).containsExactlyEntriesOf(Map.of(1, 15L));
		Result next = DataKeyRotationAccess.run(context, 10, 1);
		assertThat(next.reencrypted()).isEqualTo(10);
		Result rest = DataKeyRotationAccess.run(context, 10, 100);
		assertThat(rest.reencrypted()).isEqualTo(5);
		assertThat(rest.remaining()).isZero();
		assertThat(DataKeyRotationAccess.run(context).reencrypted()).isZero();
		assertSecrets(first);

		Map<UUID, byte[]> second = oldUsers(60);
		List<Result> together = Concurrently.run(4, (i) -> DataKeyRotationAccess.run(context, 5, 100));

		assertThat(together.stream().mapToLong(Result::reencrypted).sum()).isEqualTo(60);
		assertThat(together.stream().mapToLong(Result::failed).sum()).isZero();
		assertThat(keyIdsOf(second.keySet())).containsOnly(2);
		assertSecrets(second);
	}

	@Test
	void aValueThatCannotBeReadIsLeftAsItIsAndCountedWithoutLoggingIt(CapturedOutput output) throws Exception {
		assertRunsOnItsOwnDatabase();
		DataKeyRotationAccess.run(context);
		Map<UUID, byte[]> users = oldUsers(2);
		List<UUID> ids = new ArrayList<>(users.keySet());
		byte[] unknown = queryOne("select mfa_secret_encrypted from users where id = ?", ids.get(0));
		unknown[0] = 9; // a key this instance does not hold
		exec("update users set mfa_secret_encrypted = ? where id = ?", unknown, ids.get(0));
		// Bound to another row, as a value copied from elsewhere would be: it does not verify here.
		byte[] copied = KEY_1_ONLY.encrypt(users.get(ids.get(1)), column(MFA).binding().apply(UUID.randomUUID()));
		exec("update users set mfa_secret_encrypted = ? where id = ?", copied, ids.get(1));
		try {
			Result run = DataKeyRotationAccess.run(context);

			assertThat(run.reencrypted()).isZero();
			assertThat(run.failedByColumn().get(MFA)).isEqualTo(1);
			assertThat(run.remainingByColumn().get(MFA)).containsExactlyInAnyOrderEntriesOf(Map.of(1, 1L, 9, 1L));
			assertThat((byte[]) queryOne("select mfa_secret_encrypted from users where id = ?", ids.get(0)))
				.isEqualTo(unknown);
			assertThat((byte[]) queryOne("select mfa_secret_encrypted from users where id = ?", ids.get(1)))
				.isEqualTo(copied);
			assertThat(output).contains("Data key rotation: current key 2")
				.contains("users.mfa_secret_encrypted key 1=1,key 9=1 failed=1");
			for (byte[] value : List.of(unknown, copied)) {
				assertThat(output).doesNotContain(HexFormat.of().formatHex(value))
					.doesNotContain(Base64.getEncoder().encodeToString(value));
			}
			for (byte[] secret : users.values()) {
				assertThat(output).doesNotContain(HexFormat.of().formatHex(secret))
					.doesNotContain(Base64.getEncoder().encodeToString(secret));
			}
		}
		finally {
			exec("update users set mfa_enabled = false, mfa_secret_encrypted = null where id = any(?)",
					(Object) ids.toArray(UUID[]::new));
		}
	}

	@Test
	void everyEncryptedColumnInTheSchemaIsDeclaredForRotation() {
		Set<String> inSchema = new TreeSet<>(this.<String>queryList("""
				select table_name || '.' || column_name from information_schema.columns
				where table_schema = 'public' and data_type = 'bytea' and column_name like '%\\_encrypted'
				"""));
		Set<String> declared = encryptedColumns.stream().map(EncryptedColumn::name).collect(Collectors.toCollection(TreeSet::new));

		assertThat(declared).isEqualTo(inSchema).containsExactly(PENDING_MFA, TAG_UID, BANK_ACCOUNT, MFA);
	}

	// ---------------------------------------------------------------------------- arrangements

	/** Seeds into this class's database, with the MFA secret under the current key, as an API write would. */
	@Override
	public SeededUser seed(Role role, Scope scope, boolean mustChangePassword) {
		UUID id = UUID.randomUUID();
		String email = role.name().toLowerCase() + "-rot-" + SEQUENCE.incrementAndGet() + "-" + id.toString().substring(0, 8)
				+ "@zawed.test";
		byte[] secret = role.requiresMfa() ? randomSecret() : null;
		exec("""
				insert into users (id, email, full_name, password_hash, role, scope_type, scope_id, status,
				                   must_change_password, mfa_secret_encrypted, mfa_enabled, created_at, updated_at)
				values (?, ?, ?, ?, ?, ?, ?, 'ACTIVE', ?, ?, ?, now(), now())
				""", id, email, "Test " + role, ARGON2.encode(PASSWORD), role.name(), scope.type().name(), scope.id(),
				mustChangePassword, secret == null ? null : dataEncryptor.encrypt(secret, column(MFA).binding().apply(id)),
				secret != null);
		return new SeededUser(id, email, role, scope, secret);
	}

	/** Users with a TOTP secret under key 1, written straight into the table. @return id to secret */
	private Map<UUID, byte[]> oldUsers(int count) {
		Map<UUID, byte[]> users = new LinkedHashMap<>();
		for (int i = 0; i < count; i++) {
			UUID id = UUID.randomUUID();
			byte[] secret = randomSecret();
			exec("""
					insert into users (id, email, full_name, password_hash, role, scope_type, status, mfa_secret_encrypted,
					                   mfa_enabled, created_at, updated_at)
					values (?, ?, 'Old user', 'not-a-hash', 'OPS', 'PLATFORM', 'ACTIVE', ?, true, now(), now())
					""", id, "old-" + id + "@zawed.test", KEY_1_ONLY.encrypt(secret, column(MFA).binding().apply(id)));
			users.put(id, secret);
		}
		return users;
	}

	private void assertSecrets(Map<UUID, byte[]> users) {
		EncryptedColumn mfa = column(MFA);
		Map<UUID, Object> stored = pairs("select id, mfa_secret_encrypted from users where id = any(?)",
				(Object) users.keySet().toArray(UUID[]::new));
		assertThat(stored).hasSameSizeAs(users);
		users.forEach((id, secret) -> assertThat(KEY_2_ONLY.decrypt((byte[]) stored.get(id), mfa.binding().apply(id)))
			.isEqualTo(secret));
	}

	private UUID company() {
		UUID id = UUID.randomUUID();
		exec("""
				insert into companies (id, legal_name, name_ar, name_en, status, cooldown_minutes,
				                       low_balance_threshold_piastres, created_at, updated_at)
				values (?, 'Test Company', 'شركة اختبار', 'Test Company', 'ACTIVE', 180, 0, now(), now())
				""", id);
		UUID account = UUID.randomUUID();
		exec("insert into ledger_accounts (id, code, kind, company_id, station_id, created_at) "
				+ "values (?, ?, 'COMPANY_WALLET', ?, null, now())", account, "company:" + id + ":wallet", id);
		exec("insert into company_wallets (company_id, account_id, balance_piastres, created_at, updated_at) "
				+ "values (?, ?, 0, now(), now())", id, account);
		return id;
	}

	private String requestBankChange(String station, String number) throws Exception {
		HttpResponse<String> requested = authed("POST", "/api/v1/ops/stations/" + station + "/bank-account/changes",
				ops.accessToken(), "{\"accountNumber\":\"" + number + "\",\"bankName\":\"Bank\",\"holderName\":\"Station Co\","
						+ "\"bankLetterReference\":\"BL-" + UUID.randomUUID() + "\"}");
		assertThat(requested.statusCode()).as(requested.body()).isEqualTo(201);
		return JsonPath.read(requested.body(), "$.id");
	}

	private String enrolmentSecret(String challenge) throws Exception {
		HttpResponse<String> start = post("/api/v1/auth/mfa/enroll/start", "{\"challengeToken\":\"" + challenge + "\"}");
		assertThat(start.statusCode()).as(start.body()).isEqualTo(200);
		return JsonPath.read(start.body(), "$.secret");
	}

	private List<String> snapshotDigests(Pump pump) throws Exception {
		HttpResponse<String> snapshot = send(request("/api/v1/pos/snapshot")
			.header("Authorization", "Bearer " + pump.deviceToken())
			.GET());
		assertThat(snapshot.statusCode()).as(snapshot.body()).isEqualTo(200);
		return JsonPath.read(snapshot.body(), "$.tags[*].digest");
	}

	private String digestOf(Pump pump, Car car) throws Exception {
		return SnapshotTests.digest(tagKey(pump), car.tagUid());
	}

	private String tagKey(Pump pump) throws Exception {
		HttpResponse<String> snapshot = send(request("/api/v1/pos/snapshot")
			.header("Authorization", "Bearer " + pump.deviceToken())
			.GET());
		return JsonPath.read(snapshot.body(), "$.tagKey");
	}

	/** The BANK_ACCOUNT_REKEYED rows the database wrote for this account, oldest first. */
	private List<String> rekeyAudits(UUID account) {
		return queryList("""
				select 'key ' || (metadata->>'fromKeyId') || ' -> ' || (metadata->>'toKeyId') || ', station '
				       || (metadata->>'stationId') || ', actor ' || coalesce(actor_id::text, 'none')
				from audit_log where action = 'BANK_ACCOUNT_REKEYED' and entity_id = ? order by id
				""", account);
	}

	/** LookupHasher's hash with key 1 as the lookup key, computed here independently. */
	private static String lookupHash(String namespace, String value) throws Exception {
		Mac mac = Mac.getInstance("HmacSHA256");
		mac.init(new SecretKeySpec(Base64.getDecoder().decode(KEY_1), "HmacSHA256"));
		byte[] derived = mac.doFinal(("zawed-lookup:" + namespace).getBytes(StandardCharsets.UTF_8));
		mac.init(new SecretKeySpec(derived, "HmacSHA256"));
		return HexFormat.of().formatHex(mac.doFinal(value.getBytes(StandardCharsets.UTF_8)));
	}

	private static String lookupHash(String uid) throws Exception {
		return lookupHash("rfid-uid", uid);
	}

	private static String fingerprintOf(String lookupKey) throws Exception {
		assertThat(lookupKey).isEqualTo(KEY_1);
		return lookupHash("lookup-key-fingerprint", "zawed");
	}

	/** The same length, one character different: what a swap that passes a length check looks like. */
	private static byte[] otherOfSameLength(byte[] value) {
		byte[] other = value.clone();
		other[other.length - 1] = (byte) (other[other.length - 1] == '0' ? '1' : '0');
		return other;
	}

	// ----------------------------------------------------------------- rewriting values under key 1

	private int downgradeEverything() throws SQLException {
		int count = 0;
		for (EncryptedColumn column : encryptedColumns) {
			count += downgrade(column, ids(column));
		}
		return count;
	}

	/**
	 * Puts these rows' values back under key 1, as the owner with triggers off (the V20 guards refuse a
	 * lower key id, rightly). The plaintext and the binding stay the same.
	 */
	private int downgrade(EncryptedColumn column, List<UUID> ids) throws SQLException {
		try (Connection owner = connect(); Statement replica = owner.createStatement()) {
			replica.execute("set session_replication_role = replica");
			for (UUID id : ids) {
				byte[] value = queryOne(owner, "select " + column.column() + " from " + column.table() + " where id = ?", id);
				byte[] binding = column.binding().apply(id);
				byte[] old = KEY_1_ONLY.encrypt(dataEncryptor.decrypt(value, binding), binding);
				try (PreparedStatement update = owner.prepareStatement(
						"update " + column.table() + " set " + column.column() + " = ? where id = ?")) {
					update.setBytes(1, old);
					update.setObject(2, id);
					assertThat(update.executeUpdate()).isEqualTo(1);
				}
			}
		}
		return ids.size();
	}

	private List<UUID> ids(EncryptedColumn column) {
		return queryList("select id from " + column.table() + " where " + column.column() + " is not null order by id");
	}

	private Set<Integer> keyIds(EncryptedColumn column) {
		return new TreeSet<>(this.<Integer>queryList("select distinct data_key_id(" + column.column() + ") from "
				+ column.table() + " where " + column.column() + " is not null"));
	}

	private Set<Integer> keyIdsOf(Set<UUID> users) {
		return new TreeSet<>(this.<Integer>queryList(
				"select distinct data_key_id(mfa_secret_encrypted) from users where id = any(?)", (Object) users.toArray(UUID[]::new)));
	}

	private Map<UUID, byte[]> plaintexts(EncryptedColumn column, DataEncryptor reader) {
		Map<UUID, byte[]> plaintexts = new HashMap<>();
		pairs("select id, " + column.column() + " from " + column.table() + " where " + column.column() + " is not null")
			.forEach((id, value) -> plaintexts.put(id, reader.decrypt((byte[]) value, column.binding().apply(id))));
		return plaintexts;
	}

	private Map<UUID, Object> rowsWithoutCiphertext(EncryptedColumn column) {
		return pairs("select id, (to_jsonb(t) - '" + column.column() + "')::text from " + column.table() + " t where "
				+ column.column() + " is not null");
	}

	/** The first column (an id) to the second, in one query. */
	private static Map<UUID, Object> pairs(String sql, Object... params) {
		Map<UUID, Object> pairs = new HashMap<>();
		try (Connection c = connect(); PreparedStatement statement = c.prepareStatement(sql)) {
			bind(c, statement, params);
			try (ResultSet rs = statement.executeQuery()) {
				while (rs.next()) {
					pairs.put(rs.getObject(1, UUID.class), rs.getObject(2));
				}
			}
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
		return pairs;
	}

	private EncryptedColumn column(String name) {
		return encryptedColumns.stream().filter((c) -> c.name().equals(name)).findFirst().orElseThrow();
	}

	private void assertRunsOnItsOwnDatabase() throws SQLException {
		try (Connection connection = dataSource.getConnection()) {
			assertThat(connection.getCatalog()).isEqualTo(databaseUrl().substring(databaseUrl().lastIndexOf('/') + 1)
				.split("\\?")[0]);
		}
	}

	// ------------------------------------------------------------------------- this class's database

	static synchronized String databaseUrl() {
		if (databaseUrl == null) {
			databaseUrl = TestDatabase.newDatabase("zawed_rot");
		}
		return databaseUrl;
	}

	private static Connection connect() throws SQLException {
		return DriverManager.getConnection(databaseUrl(), TestDatabase.USERNAME, TestDatabase.PASSWORD);
	}

	private static void exec(String sql, Object... params) {
		try (Connection c = connect()) {
			update(c, sql, params);
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

	private static int update(Connection c, String sql, Object... params) throws SQLException {
		try (PreparedStatement statement = c.prepareStatement(sql)) {
			bind(c, statement, params);
			return statement.executeUpdate();
		}
	}

	/** The database's refusal of this update, or a failure when it was accepted. */
	private static String refusal(Connection c, String sql, Object... params) {
		try {
			update(c, sql, params);
		}
		catch (SQLException ex) {
			return ex.getMessage();
		}
		throw new AssertionError("The update was accepted: " + sql);
	}

	@SuppressWarnings("unchecked")
	private static <T> T queryOne(String sql, Object... params) {
		try (Connection c = connect()) {
			return (T) queryOne(c, sql, params);
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

	@SuppressWarnings("unchecked")
	private static <T> T queryOne(Connection c, String sql, Object... params) throws SQLException {
		try (PreparedStatement statement = c.prepareStatement(sql)) {
			bind(c, statement, params);
			try (ResultSet rs = statement.executeQuery()) {
				return rs.next() ? (T) rs.getObject(1) : null;
			}
		}
	}

	@SuppressWarnings("unchecked")
	private <T> List<T> queryList(String sql, Object... params) {
		try (Connection c = connect(); PreparedStatement statement = c.prepareStatement(sql)) {
			bind(c, statement, params);
			List<T> values = new ArrayList<>();
			try (ResultSet rs = statement.executeQuery()) {
				while (rs.next()) {
					values.add((T) rs.getObject(1));
				}
			}
			return values;
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

	private static void bind(Connection c, PreparedStatement statement, Object... params) throws SQLException {
		for (int i = 0; i < params.length; i++) {
			if (params[i] instanceof UUID[] ids) {
				statement.setArray(i + 1, c.createArrayOf("uuid", ids));
			}
			else if (params[i] instanceof byte[] bytes) {
				statement.setBytes(i + 1, bytes);
			}
			else {
				statement.setObject(i + 1, params[i]);
			}
		}
	}

	// -------------------------------------------------------------------------------------- TOTP

	private static byte[] randomSecret() {
		byte[] secret = new byte[20];
		RANDOM.nextBytes(secret);
		return secret;
	}

	private static byte[] concat(byte[] value, String suffix) {
		byte[] extra = suffix.getBytes(StandardCharsets.US_ASCII);
		return ByteBuffer.allocate(value.length + extra.length).put(value).put(extra).array();
	}

	/** RFC 6238 with the parameters of identity.Totp: HMAC-SHA1, 30-second steps, six digits. */
	private static String totp(byte[] secret, long step) throws Exception {
		Mac mac = Mac.getInstance("HmacSHA1");
		mac.init(new SecretKeySpec(secret, "HmacSHA1"));
		byte[] hash = mac.doFinal(ByteBuffer.allocate(8).putLong(step).array());
		int offset = hash[hash.length - 1] & 0x0f;
		int binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16) | ((hash[offset + 2] & 0xff) << 8)
				| (hash[offset + 3] & 0xff);
		return String.format("%06d", binary % 1_000_000);
	}

	private static byte[] base32Decode(String text) {
		String alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		ByteBuffer out = ByteBuffer.allocate(text.length() * 5 / 8);
		int buffer = 0;
		int bits = 0;
		for (char c : text.replace("=", "").toUpperCase().toCharArray()) {
			buffer = (buffer << 5) | alphabet.indexOf(c);
			bits += 5;
			if (bits >= 8) {
				out.put((byte) (buffer >> (bits - 8)));
				bits -= 8;
			}
		}
		return java.util.Arrays.copyOf(out.array(), out.position());
	}

}
