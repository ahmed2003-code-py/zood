package com.zawed.api;

import org.junit.jupiter.api.Test;
import org.springframework.modulith.core.ApplicationModules;

/**
 * Fails the build when a module depends on another module's internals or when two modules depend
 * on each other. Run it after adding a module or a cross-module call.
 */
class ModularityTests {

	private final ApplicationModules modules = ApplicationModules.of(ZawedApiApplication.class);

	@Test
	void modulesRespectTheirBoundaries() {
		modules.verify();
	}

}
