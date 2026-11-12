package com.zawed.api;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.context.properties.ConfigurationPropertiesScan;

/**
 * ZAWED core API: a modular monolith. Each direct sub-package of this one is a module with its own
 * public surface, and {@code ModularityTests} fails the build when one module reaches into
 * another's internals.
 */
@SpringBootApplication
@ConfigurationPropertiesScan
public class ZawedApiApplication {

	public static void main(String[] args) {
		SpringApplication.run(ZawedApiApplication.class, args);
	}

}
