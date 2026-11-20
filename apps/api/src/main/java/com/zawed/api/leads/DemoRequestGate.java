package com.zawed.api.leads;

import java.io.BufferedReader;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Map;

import jakarta.servlet.FilterChain;
import jakarta.servlet.ReadListener;
import jakarta.servlet.ServletException;
import jakarta.servlet.ServletInputStream;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletRequestWrapper;
import jakarta.servlet.http.HttpServletResponse;

import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.http.HttpStatus;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;
import org.springframework.web.servlet.HandlerExceptionResolver;

import com.zawed.api.platform.ratelimit.PostgresRateLimiter;
import com.zawed.api.platform.ratelimit.RateLimitedException;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.ClientInfo;

/**
 * The public demo-request form's front door, before the body is parsed. Every POST counts against the
 * sender's address, valid or not, so a script cannot probe validation for free; then the body is read
 * once, up to {@link #MAX_BODY_BYTES}, and a larger one is refused (413) before JSON sees it.
 *
 * <p>The address is the one the platform's proxy reports (ClientInfo, {@code forward-headers-strategy}
 * in the deployed profile): an {@code X-Forwarded-For} the client sends itself is not trusted. The
 * counters are in PostgreSQL ({@link PostgresRateLimiter}), so a deploy or a second instance gives no
 * fresh budget.
 */
@Component
class DemoRequestGate extends OncePerRequestFilter {

	static final String PATH = "/api/v1/public/demo-requests";

	static final int MAX_BODY_BYTES = 4 * 1024;

	static final int PER_ADDRESS_PER_HOUR = 5;

	static final int PER_ADDRESS_PER_DAY = 20;

	private final PostgresRateLimiter limiter;
	private final Leads leads;
	private final HandlerExceptionResolver resolver;

	DemoRequestGate(PostgresRateLimiter limiter, Leads leads,
			@Qualifier("handlerExceptionResolver") HandlerExceptionResolver resolver) {
		this.limiter = limiter;
		this.leads = leads;
		this.resolver = resolver;
	}

	@Override
	protected boolean shouldNotFilter(HttpServletRequest request) {
		return !("POST".equals(request.getMethod()) && PATH.equals(request.getRequestURI()));
	}

	@Override
	protected void doFilterInternal(HttpServletRequest request, HttpServletResponse response, FilterChain chain)
			throws ServletException, IOException {
		byte[] body;
		try {
			String address = ClientInfo.limitKey(request);
			try {
				limiter.acquire("demo-ip-hour", address, PER_ADDRESS_PER_HOUR, Duration.ofHours(1));
				limiter.acquire("demo-ip-day", address, PER_ADDRESS_PER_DAY, Duration.ofDays(1));
			}
			catch (RateLimitedException ex) {
				leads.count("rate_limited_address");
				throw ex;
			}
			if (request.getContentLengthLong() > MAX_BODY_BYTES) {
				throw tooLarge();
			}
			body = request.getInputStream().readNBytes(MAX_BODY_BYTES + 1);
			if (body.length > MAX_BODY_BYTES) {
				throw tooLarge();
			}
		}
		catch (ApiException ex) {
			resolver.resolveException(request, response, null, ex);
			return;
		}
		chain.doFilter(new ReadBody(request, body), response);
	}

	private static ApiException tooLarge() {
		return new ApiException(HttpStatus.valueOf(413), "PAYLOAD_TOO_LARGE", "The request body is too large", null,
				Map.of("maxBytes", MAX_BODY_BYTES));
	}

	/** The request with the body already read, so it is read from the network once and never past the cap. */
	private static final class ReadBody extends HttpServletRequestWrapper {

		private final byte[] body;

		ReadBody(HttpServletRequest request, byte[] body) {
			super(request);
			this.body = body;
		}

		@Override
		public ServletInputStream getInputStream() {
			InputStream in = new ByteArrayInputStream(body);
			return new ServletInputStream() {
				@Override
				public int read() throws IOException {
					return in.read();
				}

				@Override
				public int read(byte[] buffer, int offset, int length) throws IOException {
					return in.read(buffer, offset, length);
				}

				@Override
				public boolean isFinished() {
					try {
						return in.available() == 0;
					}
					catch (IOException ex) {
						return true;
					}
				}

				@Override
				public boolean isReady() {
					return true;
				}

				@Override
				public void setReadListener(ReadListener listener) {
					throw new UnsupportedOperationException("Not used: the body is already read");
				}
			};
		}

		@Override
		public BufferedReader getReader() {
			String encoding = getCharacterEncoding();
			Charset charset = encoding == null ? StandardCharsets.UTF_8 : Charset.forName(encoding);
			return new BufferedReader(new InputStreamReader(getInputStream(), charset));
		}

		@Override
		public int getContentLength() {
			return body.length;
		}

		@Override
		public long getContentLengthLong() {
			return body.length;
		}

	}

}
