package com.zawed.api.network;

import java.time.Duration;

import jakarta.servlet.http.HttpServletRequest;

import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.security.core.Authentication;
import org.springframework.stereotype.Component;

import com.zawed.api.network.AgentSessions.AgentRef;
import com.zawed.api.network.DeviceChannel.DeviceRef;
import com.zawed.api.platform.ratelimit.RateLimiter;
import com.zawed.api.platform.security.PosAuthenticator;
import com.zawed.api.platform.web.ApiException;

/**
 * The POS chain's check (B4 contract §1): {@code Authorization: Bearer <device token>} names an
 * ACTIVE device, and {@code X-Agent-Session}, when sent, names a live session of an ACTIVE agent of
 * that device's station opened on that same device.
 */
@Component
class PosCredentials implements PosAuthenticator {

	static final String AGENT_HEADER = "X-Agent-Session";
	/** A busy pump does a fueling every minute or two; this is far above that and stops a runaway client. */
	static final int REQUESTS_PER_MINUTE = 120;

	private final DeviceChannel devices;
	private final AgentSessions agents;
	private final RateLimiter limiter;

	PosCredentials(DeviceChannel devices, AgentSessions agents, RateLimiter limiter) {
		this.devices = devices;
		this.agents = agents;
		this.limiter = limiter;
	}

	@Override
	public Authentication authenticate(HttpServletRequest request) {
		String header = request.getHeader(HttpHeaders.AUTHORIZATION);
		String token = header != null && header.regionMatches(true, 0, "Bearer ", 0, 7) ? header.substring(7).strip()
				: null;
		DeviceRef device = (token == null ? java.util.Optional.<DeviceRef>empty() : devices.verify(token))
			.orElseThrow(() -> new ApiException(HttpStatus.UNAUTHORIZED, "DEVICE_UNAUTHENTICATED",
					"The device could not be authenticated"));
		limiter.acquire("pos:" + device.deviceId(), REQUESTS_PER_MINUTE, Duration.ofMinutes(1));
		String session = request.getHeader(AGENT_HEADER);
		AgentRef agent = null;
		if (session != null && !session.isBlank()) {
			agent = agents.verify(device, session.strip())
				.orElseThrow(() -> new ApiException(HttpStatus.UNAUTHORIZED, "AGENT_SESSION_INVALID",
						"The agent session has ended; sign in again"));
			agents.touch(session.strip());
		}
		devices.touch(device.deviceId());
		return new PosPrincipal(device.deviceId(), device.code(), device.stationId(), device.operatorId(),
				agent == null ? null : agent.agentId(), agent == null ? null : agent.workerId(),
				agent == null ? null : agent.fullName(), agent != null && agent.mustChangePin())
			.toAuthentication();
	}

	static DeviceRef deviceOf(PosPrincipal principal) {
		return new DeviceRef(principal.deviceId(), principal.deviceCode(), principal.stationId(), principal.operatorId());
	}

	static AgentRef agentOf(PosPrincipal principal) {
		return new AgentRef(principal.agentId(), principal.agentWorkerId(), principal.agentName(),
				principal.mustChangePin());
	}

}
