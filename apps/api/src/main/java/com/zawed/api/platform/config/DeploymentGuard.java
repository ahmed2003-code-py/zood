package com.zawed.api.platform.config;

import org.springframework.beans.factory.InitializingBean;
import org.springframework.core.env.Environment;
import org.springframework.core.env.Profiles;
import org.springframework.stereotype.Component;

/**
 * Refuses to start an internet-facing instance that is configured like a development machine.
 *
 * <p>The {@code deployed} profile reads {@code ZAWED_ENVIRONMENT} and {@code ZAWED_CORS_ALLOWED_ORIGINS}
 * with no defaults. Without this check, a value of {@code local} (or a forgotten variable that
 * happened to resolve) would switch off the https-only and no-localhost CORS checks in
 * {@link ZawedProperties} while the API is on the internet.
 */
@Component
class DeploymentGuard implements InitializingBean {

	static final String DEPLOYED_PROFILE = "deployed";

	private final Environment springEnvironment;
	private final ZawedProperties properties;

	DeploymentGuard(Environment springEnvironment, ZawedProperties properties) {
		this.springEnvironment = springEnvironment;
		this.properties = properties;
	}

	@Override
	public void afterPropertiesSet() {
		boolean deployed = springEnvironment.acceptsProfiles(Profiles.of(DEPLOYED_PROFILE));
		if (deployed && !properties.environment().isDeployed()) {
			throw new IllegalStateException("The '" + DEPLOYED_PROFILE + "' profile is active but zawed.environment is "
					+ properties.environment() + ". Set ZAWED_ENVIRONMENT to dev, staging or production.");
		}
		if (!deployed && properties.environment().isDeployed()) {
			throw new IllegalStateException("zawed.environment is " + properties.environment() + " but the '"
					+ DEPLOYED_PROFILE + "' profile is not active. Set SPRING_PROFILES_ACTIVE=" + DEPLOYED_PROFILE + ".");
		}
	}

}
