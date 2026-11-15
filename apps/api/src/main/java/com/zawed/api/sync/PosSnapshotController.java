package com.zawed.api.sync;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.CacheControl;
import org.springframework.http.ResponseEntity;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.network.PosPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.sync.Snapshots.PosSnapshot;

/** The snapshot a POS caches to work offline (B5 contract §2). Device token only; no agent needed. */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class PosSnapshotController {

	private final Snapshots snapshots;

	PosSnapshotController(Snapshots snapshots) {
		this.snapshots = snapshots;
	}

	@GetMapping("/pos/snapshot")
	@Tag(name = "pos")
	@Operation(operationId = "getPosSnapshot",
			summary = "What this device needs to decide fuelings offline: tags (as digests), vehicles, limits, prices, wallet allowances")
	ResponseEntity<PosSnapshot> snapshot(@AuthenticationPrincipal PosPrincipal device) {
		return ResponseEntity.ok().cacheControl(CacheControl.noStore()).body(snapshots.snapshotFor(device));
	}

}
