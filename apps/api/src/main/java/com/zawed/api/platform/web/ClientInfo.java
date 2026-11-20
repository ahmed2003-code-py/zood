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

	/**
	 * The subject of a per-address limit: an IPv4 address as it is, an IPv6 address by its /64. One
	 * IPv6 host is usually handed a whole /64 and can pick any address in it, so counting full IPv6
	 * addresses would give it billions of budgets. Hash it before storing (the limiters do).
	 */
	public static String limitKey(HttpServletRequest request) {
		return limitKeyOf(request.getRemoteAddr());
	}

	static String limitKeyOf(String address) {
		if (address == null || address.isBlank()) {
			return "unknown";
		}
		try {
			byte[] bytes = InetAddress.ofLiteral(address).getAddress();
			if (bytes.length == 4) {
				return InetAddress.getByAddress(bytes).getHostAddress();
			}
			byte[] prefix = new byte[16];
			System.arraycopy(bytes, 0, prefix, 0, 8);
			return InetAddress.getByAddress(prefix).getHostAddress() + "/64";
		}
		catch (IllegalArgumentException | java.net.UnknownHostException ex) {
			return "unknown";
		}
	}

	/**
	 * The wider network of an IPv6 client, its /48 (what a site or a small provider customer is usually
	 * given: 65,536 /64s), for a second, looser limit; null for IPv4, whose address is already the network.
	 */
	public static String networkKey(HttpServletRequest request) {
		return networkKeyOf(request.getRemoteAddr());
	}

	static String networkKeyOf(String address) {
		if (address == null || address.isBlank()) {
			return null;
		}
		try {
			byte[] bytes = InetAddress.ofLiteral(address).getAddress();
			if (bytes.length == 4) {
				return null;
			}
			byte[] prefix = new byte[16];
			System.arraycopy(bytes, 0, prefix, 0, 6);
			return InetAddress.getByAddress(prefix).getHostAddress() + "/48";
		}
		catch (IllegalArgumentException | java.net.UnknownHostException ex) {
			return null;
		}
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
