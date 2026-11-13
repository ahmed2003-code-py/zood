package com.zawed.api.identity;

import java.time.Duration;

import jakarta.servlet.http.Cookie;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.http.HttpHeaders;
import org.springframework.http.ResponseCookie;
import org.springframework.stereotype.Component;

import com.zawed.api.identity.IdentityRecords.Door;
import com.zawed.api.platform.config.ZawedProperties;

/**
 * The refresh cookie (contract §3): HttpOnly so no script can read it, Secure when deployed,
 * SameSite=Strict so no other site can make the browser send it, and scoped to the auth path so it
 * travels with refresh and logout and nothing else. The admin door has its own cookie and path.
 *
 * <p>Beside it, {@code zawed_session=1} is readable by the page and holds no secret: it only tells
 * the web app, after a reload, that a refresh is worth trying.
 */
@Component
class AuthCookies {

	static final String USER_COOKIE = "zawed_rt";
	static final String ADMIN_COOKIE = "zawed_admin_rt";
	static final String HINT_COOKIE = "zawed_session";

	private final boolean secure;

	AuthCookies(ZawedProperties properties) {
		this.secure = properties.security().cookieSecure();
	}

	void set(HttpServletResponse response, Door door, String refreshToken) {
		Duration lifetime = Sessions.ABSOLUTE;
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(nameOf(door), refreshToken, pathOf(door), true, lifetime));
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(HINT_COOKIE, "1", "/", false, lifetime));
	}

	void clear(HttpServletResponse response, Door door) {
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(nameOf(door), "", pathOf(door), true, Duration.ZERO));
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(HINT_COOKIE, "", "/", false, Duration.ZERO));
	}

	String read(HttpServletRequest request, Door door) {
		Cookie[] cookies = request.getCookies();
		if (cookies == null) {
			return null;
		}
		for (Cookie cookie : cookies) {
			if (nameOf(door).equals(cookie.getName())) {
				return cookie.getValue();
			}
		}
		return null;
	}

	private String cookie(String name, String value, String path, boolean httpOnly, Duration maxAge) {
		return ResponseCookie.from(name, value)
			.httpOnly(httpOnly)
			.secure(secure)
			.sameSite("Strict")
			.path(path)
			.maxAge(maxAge)
			.build()
			.toString();
	}

	private static String nameOf(Door door) {
		return door == Door.ADMIN ? ADMIN_COOKIE : USER_COOKIE;
	}

	private static String pathOf(Door door) {
		return door == Door.ADMIN ? "/api/v1/admin/auth" : "/api/v1/auth";
	}

}
