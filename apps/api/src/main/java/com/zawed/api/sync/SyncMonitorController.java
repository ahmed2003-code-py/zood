package com.zawed.api.sync;

import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;
import com.zawed.api.sync.SyncMonitor.SyncBatch;
import com.zawed.api.sync.SyncMonitor.DeviceSync;

/** The sync monitor (B5 contract §5). */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class SyncMonitorController {

	private final SyncMonitor monitor;

	SyncMonitorController(SyncMonitor monitor) {
		this.monitor = monitor;
	}

	@GetMapping("/ops/sync/devices")
	@Tag(name = "sync")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listSyncDevices", summary = "Active devices with their last sync and last batch")
	Page<DeviceSync> opsDevices(@RequestParam(required = false) UUID operatorId,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) Boolean stale,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return monitor.devices(operatorId, stationId, stale, cursor, limit);
	}

	@GetMapping("/ops/sync/batches")
	@Tag(name = "sync")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listSyncBatches", summary = "Batches received from devices, newest first")
	Page<SyncBatch> opsBatches(@RequestParam(required = false) UUID deviceId,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return monitor.batches(null, deviceId, cursor, limit);
	}

	@GetMapping("/station/sync/devices")
	@Tag(name = "station portal")
	@RequiresPermission(Permission.STATION_READ)
	@Operation(operationId = "listStationSyncDevices", summary = "The operator's active devices with their last sync")
	Page<DeviceSync> stationDevices(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) UUID stationId, @RequestParam(required = false) Boolean stale,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		return monitor.devices(principal.scope().id(), stationId, stale, cursor, limit);
	}

}
