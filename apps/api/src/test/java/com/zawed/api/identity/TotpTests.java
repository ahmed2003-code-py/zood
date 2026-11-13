package com.zawed.api.identity;

import static org.assertj.core.api.Assertions.assertThat;

import java.nio.charset.StandardCharsets;

import org.junit.jupiter.api.Test;

/** RFC 6238 Appendix B test vectors (SHA-1, 8 digits) and the 6-digit form apps use. */
class TotpTests {

	private static final byte[] RFC_SECRET = "12345678901234567890".getBytes(StandardCharsets.US_ASCII);

	@Test
	void matchesTheRfcVectors() {
		assertThat(Totp.codeAt(RFC_SECRET, Totp.stepAt(59), 8, "HmacSHA1")).isEqualTo("94287082");
		assertThat(Totp.codeAt(RFC_SECRET, Totp.stepAt(1111111109), 8, "HmacSHA1")).isEqualTo("07081804");
		assertThat(Totp.codeAt(RFC_SECRET, Totp.stepAt(1111111111), 8, "HmacSHA1")).isEqualTo("14050471");
		assertThat(Totp.codeAt(RFC_SECRET, Totp.stepAt(1234567890), 8, "HmacSHA1")).isEqualTo("89005924");
		assertThat(Totp.codeAt(RFC_SECRET, Totp.stepAt(2000000000), 8, "HmacSHA1")).isEqualTo("69279037");
	}

	@Test
	void acceptsOneStepOfClockDriftAndNoMore() {
		long now = 1_800_000_000L;
		long step = Totp.stepAt(now);
		assertThat(Totp.matchingStep(RFC_SECRET, Totp.codeAt(RFC_SECRET, step), now)).hasValue(step);
		assertThat(Totp.matchingStep(RFC_SECRET, Totp.codeAt(RFC_SECRET, step - 1), now)).hasValue(step - 1);
		assertThat(Totp.matchingStep(RFC_SECRET, Totp.codeAt(RFC_SECRET, step + 1), now)).hasValue(step + 1);
		assertThat(Totp.matchingStep(RFC_SECRET, Totp.codeAt(RFC_SECRET, step - 2), now)).isEmpty();
	}

	@Test
	void refusesAnythingThatIsNotSixDigits() {
		assertThat(Totp.matchingStep(RFC_SECRET, "12345", 0)).isEmpty();
		assertThat(Totp.matchingStep(RFC_SECRET, "abcdef", 0)).isEmpty();
		assertThat(Totp.matchingStep(RFC_SECRET, null, 0)).isEmpty();
	}

	@Test
	void writesTheKeyAppsExpect() {
		assertThat(Totp.base32(RFC_SECRET)).isEqualTo("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
		assertThat(Totp.otpauthUri("ZAWED", "ops@zawed.test", RFC_SECRET)).isEqualTo(
				"otpauth://totp/ZAWED%3Aops%40zawed.test?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=ZAWED&algorithm=SHA1&digits=6&period=30");
	}

}
