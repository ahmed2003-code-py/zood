package com.zawed.api.identity;

import java.time.Duration;

import jakarta.servlet.http.Cookie;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.http.HttpHeaders;
import org.springframework.http.ResponseCookie;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;

/**
 * The refresh cookie (contract §3): HttpOnly so no script can read it, Secure when deployed,
 * SameSite=Strict so no other site can make the browser send it, and scoped to the auth path so it
 * travels with refresh and logout and nothing else. One cookie for every role since B7 W10: the
 * Super Admin's separate cookie went with its separate sign-in (ADR 0008 amendment).
 *
 * <p>Beside it, {@code zawed_session=1} is readable by the page and holds no secret: it only tells
 * the web app, after a reload, that a refresh is worth trying.
 */
@Component
class AuthCookies {

	static final String USER_COOKIE = "zawed_rt";
	static final String HINT_COOKIE = "zawed_session";
	private static final String PATH = "/api/v1/auth";

	private final boolean secure;

	AuthCookies(ZawedProperties properties) {
		this.secure = properties.security().cookieSecure();
	}

	void set(HttpServletResponse response, String refreshToken) {
		Duration lifetime = Sessions.ABSOLUTE;
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(USER_COOKIE, refreshToken, PATH, true, lifetime));
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(HINT_COOKIE, "1", "/", false, lifetime));
	}

	void clear(HttpServletResponse response) {
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(USER_COOKIE, "", PATH, true, Duration.ZERO));
		response.addHeader(HttpHeaders.SET_COOKIE, cookie(HINT_COOKIE, "", "/", false, Duration.ZERO));
	}

	String read(HttpServletRequest request) {
		Cookie[] cookies = request.getCookies();
		if (cookies == null) {
			return null;
		}
		for (Cookie cookie : cookies) {
			if (USER_COOKIE.equals(cookie.getName())) {
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

}
