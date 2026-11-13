package com.zawed.api.platform.web;

import java.net.InetAddress;

import jakarta.servlet.http.HttpServletRequest;

/**
 * Facts about the caller that are safe to store.
 *
 * <p>The client address is the one the platform's proxy reports ({@code forward-headers-strategy}).
 * Only a prefix is ever stored (/24 for IPv4, /48 for IPv6): enough to recognise a network in an
 * investigation, not enough to identify a person's connection.
 */
public final class ClientInfo {

	private static final int MAX_USER_AGENT = 300;

	private ClientInfo() {
	}

	/** The full address, for rate limiting in memory only. Never store it. */
	public static String address(HttpServletRequest request) {
		return request.getRemoteAddr();
	}

	public static String addressPrefix(HttpServletRequest request) {
		return prefixOf(request.getRemoteAddr());
	}

	static String prefixOf(String address) {
		if (address == null || address.isBlank()) {
			return null;
		}
		try {
			// Only numeric addresses reach here (no DNS lookup for literals).
			byte[] bytes = InetAddress.ofLiteral(address).getAddress();
			if (bytes.length == 4) {
				return (bytes[0] & 0xff) + "." + (bytes[1] & 0xff) + "." + (bytes[2] & 0xff) + ".0/24";
			}
			return String.format("%x:%x:%x::/48", ((bytes[0] & 0xff) << 8) | (bytes[1] & 0xff),
					((bytes[2] & 0xff) << 8) | (bytes[3] & 0xff), ((bytes[4] & 0xff) << 8) | (bytes[5] & 0xff));
		}
		catch (IllegalArgumentException ex) {
			return null;
		}
	}

	public static String userAgent(HttpServletRequest request) {
		String agent = request.getHeader("User-Agent");
		if (agent == null) {
			return null;
		}
		String clean = agent.replaceAll("\\p{Cntrl}", "");
		return clean.length() > MAX_USER_AGENT ? clean.substring(0, MAX_USER_AGENT) : clean;
	}

}
