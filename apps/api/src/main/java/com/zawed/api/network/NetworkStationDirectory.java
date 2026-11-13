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
		return jdbc.sql("select * from stations where id = :id")
			.param("id", stationId)
			.query(Station::map)
			.optional()
			.map((s) -> new StationRef(s.id(), s.operatorId(), s.nameAr(), s.nameEn(), ZoneId.of(s.timeZone()),
					s.fuelTypes(), s.status() == StationStatus.ACTIVE));
	}

}
