package com.zawed.api.platform.crypto;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;

import org.flywaydb.core.api.callback.Callback;
import org.flywaydb.core.api.callback.Context;
import org.flywaydb.core.api.callback.Event;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;

/**
 * Records the deployment's data key state in {@code data_key_state} (V20, ADR 0027) at every start,
 * right after the migrations, on Flyway's own connection: the owner, not the runtime role, which has
 * no grant on that table.
 *
 * <ul>
 * <li><b>The lookup key's fingerprint.</b> The first start records an HMAC of a fixed text under the
 * lookup key (never the key). Every later start must have the same fingerprint, or it does not start:
 * a lookup key that is not the one the stored hashes were made with would silently break every tag
 * lookup, every pending activation code, every receipt link already sent and every device's tag
 * digest key. {@code ZawedProperties} catches the same mistake earlier while key 1 is in the ring.
 * <li><b>The current data key id.</b> The database accepts a re-encryption only into that id, so
 * outside a rotation no value can be rekeyed at all. The last instance to start sets it; a rollback of
 * the configuration sets it back.
 * </ul>
 *
 * <p>Spring Boot registers every {@link Callback} bean with Flyway; a failure here fails the migration
 * step, so the instance never serves a request.
 */
@Component
class DataKeyState implements Callback {

	private static final Logger LOG = LoggerFactory.getLogger(DataKeyState.class);

	/** A namespace of its own: the fingerprint can never equal any lookup hash the application stores. */
	static final String FINGERPRINT_NAMESPACE = "lookup-key-fingerprint";

	static final String MISMATCH = "ZAWED_LOOKUP_KEY is not the key this database's lookup hashes were made with "
			+ "(its fingerprint differs from the one recorded in data_key_state at the first start). Set it to the "
			+ "original ZAWED_DATA_KEY (ADR 0027); starting with it would break every tag lookup, activation code, "
			+ "receipt link and device tag digest";

	private final int currentKeyId;
	private final String fingerprint;

	DataKeyState(ZawedProperties properties, LookupHasher lookup) {
		this.currentKeyId = properties.security().currentDataKeyId();
		this.fingerprint = fingerprint(lookup);
	}

	/** The fingerprint of the lookup key this hasher holds. */
	static String fingerprint(LookupHasher lookup) {
		return lookup.hash(FINGERPRINT_NAMESPACE, "zawed");
	}

	@Override
	public boolean supports(Event event, Context context) {
		return event == Event.AFTER_MIGRATE;
	}

	@Override
	public boolean canHandleInTransaction(Event event, Context context) {
		return true;
	}

	@Override
	public void handle(Event event, Context context) {
		try {
			record(context.getConnection());
		}
		catch (SQLException ex) {
			throw new IllegalStateException("The data key state could not be recorded", ex);
		}
	}

	@Override
	public String getCallbackName() {
		return "DataKeyState";
	}

	void record(Connection connection) throws SQLException {
		// Two instances starting at once on an empty table: one insert wins, both then compare with it.
		try (PreparedStatement insert = connection.prepareStatement("""
				insert into data_key_state (singleton, current_key_id, lookup_key_fingerprint, fingerprint_recorded_at,
				                            updated_at)
				values (true, ?, ?, now(), now())
				on conflict (singleton) do nothing
				""")) {
			insert.setInt(1, currentKeyId);
			insert.setString(2, fingerprint);
			if (insert.executeUpdate() == 1) {
				LOG.info("Data key state: lookup key fingerprint recorded, current data key {}", currentKeyId);
				return;
			}
		}
		String recorded;
		int recordedKeyId;
		try (PreparedStatement select = connection
			.prepareStatement("select lookup_key_fingerprint, current_key_id from data_key_state for update");
				ResultSet rs = select.executeQuery()) {
			rs.next();
			recorded = rs.getString(1);
			recordedKeyId = rs.getInt(2);
		}
		if (!MessageDigest.isEqual(recorded.getBytes(StandardCharsets.US_ASCII),
				fingerprint.getBytes(StandardCharsets.US_ASCII))) {
			throw new IllegalStateException(MISMATCH);
		}
		if (recordedKeyId != currentKeyId) {
			try (PreparedStatement update = connection
				.prepareStatement("update data_key_state set current_key_id = ?, updated_at = now()")) {
				update.setInt(1, currentKeyId);
				update.executeUpdate();
			}
			LOG.info("Data key state: current data key {} (was {})", currentKeyId, recordedKeyId);
		}
	}

}
