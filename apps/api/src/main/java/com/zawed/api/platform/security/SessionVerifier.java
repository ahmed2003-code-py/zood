package com.zawed.api.platform.security;

import java.util.Optional;
import java.util.UUID;

/**
 * Checks, on every request, that the session an access token names is still usable and returns
 * the principal as the database knows it now. Implemented by the identity module; the security
 * filter chain in {@code platform} depends only on this interface.
 */
public interface SessionVerifier {

	/** Empty when the session is unknown, revoked or expired, or its user is no longer active. */
	Optional<ZawedPrincipal> verify(UUID sessionId, UUID userId);

}
