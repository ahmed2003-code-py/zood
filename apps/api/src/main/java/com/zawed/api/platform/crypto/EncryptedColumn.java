package com.zawed.api.platform.crypto;

import java.util.Objects;
import java.util.UUID;
import java.util.function.Function;
import java.util.regex.Pattern;

/**
 * A column that holds {@link DataEncryptor} values, declared as a bean by the module that owns it, so
 * that {@link DataKeyRotation} can re-encrypt it without depending on that module.
 *
 * <p>The table's key is its {@code id} (uuid). {@code binding} must return exactly the associated data
 * the owning module's own code passes for that row: the re-encrypted value is bound to it, so any
 * other binding would make the value unreadable where it is used. Point it at the module's own
 * binding method, never at a copy.
 *
 * @param table the table, a plain lower-case SQL identifier
 * @param column the bytea column, a plain lower-case SQL identifier
 * @param binding the associated data for the row with this id
 */
public record EncryptedColumn(String table, String column, Function<UUID, byte[]> binding) {

	// The names are put into SQL text, so they are checked here, once, at startup.
	private static final Pattern IDENTIFIER = Pattern.compile("[a-z_][a-z0-9_]{0,62}");

	public EncryptedColumn {
		if (table == null || !IDENTIFIER.matcher(table).matches() || column == null
				|| !IDENTIFIER.matcher(column).matches()) {
			throw new IllegalArgumentException("An encrypted column is named by plain lower-case identifiers");
		}
		Objects.requireNonNull(binding, "binding");
	}

	/** {@code table.column}, as the progress log names it. */
	public String name() {
		return table + "." + column;
	}

}
