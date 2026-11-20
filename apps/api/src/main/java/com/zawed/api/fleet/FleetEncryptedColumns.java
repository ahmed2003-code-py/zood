package com.zawed.api.fleet;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

import com.zawed.api.platform.crypto.EncryptedColumn;

/** The fleet's encrypted columns, for the data-key rotation job (ADR 0027). */
@Configuration(proxyBeanMethods = false)
class FleetEncryptedColumns {

	/** Tag UIDs, bound exactly as {@link Tags} writes and {@link FleetSnapshotReader} reads them. */
	@Bean
	EncryptedColumn tagUidColumn() {
		return new EncryptedColumn("rfid_tags", "uid_encrypted", Tags::associatedData);
	}

}
