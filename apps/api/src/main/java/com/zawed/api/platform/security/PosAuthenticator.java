package com.zawed.api.platform.security;

import jakarta.servlet.http.HttpServletRequest;

import org.springframework.security.core.Authentication;

import com.zawed.api.platform.web.ApiException;

/**
 * Identifies the device, and the agent signed in on it, behind a request to {@code /api/v1/pos/**}
 * (B4 contract §1). Implemented by the network module, which owns devices and agents; the security
 * chain only calls it, so platform code does not depend on a business module.
 *
 * <p>The authentication returned carries {@link #DEVICE}, plus {@link #AGENT} for an agent who may
 * work or {@link #AGENT_PIN_CHANGE} for one who must first replace the PIN Ops gave them.
 */
public interface PosAuthenticator {

	String DEVICE = "POS_DEVICE";

	String AGENT = "POS_AGENT";

	String AGENT_PIN_CHANGE = "POS_AGENT_PIN_CHANGE";

	/**
	 * The device and agent behind the request.
	 * @throws ApiException 401 when the device token, or an agent session the request names, is
	 * missing, invalid or no longer live
	 */
	Authentication authenticate(HttpServletRequest request);

}
