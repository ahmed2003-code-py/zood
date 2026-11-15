package com.zawed.api.network;

import java.time.Instant;
import java.util.Optional;
import java.util.UUID;

/**
 * Who was signed in on a device at a past instant (B5 contract §3). An offline capture names its
 * agent in the request body, and a request body is never trusted for who did something: the agent
 * counts only when the server's own record shows a session of that agent on that device covering
 * the capture time. That is how a fueling taken without a network is still tied to a person.
 */
public interface AgentShifts {

	/** The agent as the fueling row records them. */
	record AgentOnDevice(UUID agentId, String workerId) {
	}

	/**
	 * The agent, if they belong to {@code stationId} and had a session on {@code deviceId} that was
	 * open at {@code at}: started by then, and not signed out, revoked or expired before it.
	 *
	 * <p>Reads the session history, so it must run in the caller's transaction under the platform
	 * scope (agent sessions are platform-only under row-level security); in any other scope it finds
	 * nothing and the capture is refused.
	 */
	Optional<AgentOnDevice> onDeviceAt(UUID deviceId, UUID stationId, UUID agentId, Instant at);

	/**
	 * The agent, if they belong to {@code stationId}, whatever their sessions. A capture whose agent
	 * is of the station but whose session cannot be confirmed (it ended elsewhere while the terminal
	 * was offline) is still recorded, flagged; an agent of another station is not (B5 review 1).
	 */
	Optional<AgentOnDevice> ofStation(UUID stationId, UUID agentId);

}
