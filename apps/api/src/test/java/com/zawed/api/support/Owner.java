package com.zawed.api.support;

import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.SQLException;

/**
 * Direct access to the test database as its owner, bypassing the application and row-level
 * security. For arranging data and inspecting results only; never for asserting what the
 * application is allowed to do (that goes through the API or through {@code zawed_app}).
 */
public final class Owner {

	private Owner() {
	}

	public static Connection connect() throws SQLException {
		return DriverManager.getConnection(TestDatabase.jdbcUrl(), TestDatabase.USERNAME, TestDatabase.PASSWORD);
	}

	public static int update(String sql, Object... params) {
		try (Connection connection = connect(); PreparedStatement statement = connection.prepareStatement(sql)) {
			for (int i = 0; i < params.length; i++) {
				statement.setObject(i + 1, params[i]);
			}
			return statement.executeUpdate();
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

	public static Object queryOne(String sql, Object... params) {
		try (Connection connection = connect(); PreparedStatement statement = connection.prepareStatement(sql)) {
			for (int i = 0; i < params.length; i++) {
				statement.setObject(i + 1, params[i]);
			}
			try (var rs = statement.executeQuery()) {
				return rs.next() ? rs.getObject(1) : null;
			}
		}
		catch (SQLException ex) {
			throw new IllegalStateException(ex);
		}
	}

}
