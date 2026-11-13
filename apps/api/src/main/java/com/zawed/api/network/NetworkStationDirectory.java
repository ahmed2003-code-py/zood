package com.zawed.api.network;

import java.time.ZoneId;
import java.util.Optional;
import java.util.UUID;

import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

import com.zawed.api.network.Operators.Station;
import com.zawed.api.network.Operators.StationStatus;

@Component
class NetworkStationDirectory implements StationDirectory {

	private final JdbcClient jdbc;

	NetworkStationDirectory(JdbcClient jdbc) {
		this.jdbc = jdbc;
	}

	@Override
	public Optional<StationRef> findStation(UUID stationId) {
		// Open for fuelings only while both the station and its operator are active: suspending an
		// operator stops its stations (Operators.setStatus).
		return jdbc.sql("""
				select s.*, o.status as operator_status
				from stations s join station_operators o on o.id = s.operator_id
				where s.id = :id
				""")
			.param("id", stationId)
			.query((rs, n) -> {
				Station s = Station.map(rs, n);
				return new StationRef(s.id(), s.operatorId(), s.nameAr(), s.nameEn(), ZoneId.of(s.timeZone()),
						s.fuelTypes(), s.status() == StationStatus.ACTIVE && "ACTIVE".equals(rs.getString("operator_status")));
			})
			.optional();
	}

}
