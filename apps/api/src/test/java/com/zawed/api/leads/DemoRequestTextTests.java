package com.zawed.api.leads;

import static org.assertj.core.api.Assertions.assertThat;

import java.time.Instant;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

import com.zawed.api.leads.Leads.Submission;

/** How the demo form's text is cleaned (u1-leads-contract.md §2), and when a request is set aside (§3). */
class DemoRequestTextTests {

	@ParameterizedTest
	@CsvSource(delimiter = '|', value = { "01001234567|01001234567", "+201001234567|01001234567",
			"+20 100 123 4567|01001234567", "0020-100-123-4567|01001234567", "00201001234567|01001234567",
			"201001234567|01001234567", "1001234567|01001234567", "(010) 0123.4567|01001234567",
			"٠١٠٠١٢٣٤٥٦٧|01001234567", "۰۱۲۰۱۲۳۴۵۶۷|01201234567", "+٢٠ ١١٥ ١٢٣ ٤٥٦٧|01151234567",
			"015 5555 5555|01555555555", "0100 123 4567|01001234567" })
	void egyptianMobilesAreNormalisedTo01XXXXXXXXX(String typed, String stored) {
		assertThat(DemoRequestText.phone(typed)).isEqualTo(stored).matches(DemoRequestText.EGYPTIAN_MOBILE);
	}

	@ParameterizedTest
	@ValueSource(strings = { "01301234567", "01401234567", "0100123456", "010012345678", "0223456789",
			"+201301234567", "+1 202 555 0100", "01001234567x", "phone", "" })
	void anythingElseStaysInvalid(String typed) {
		assertThat(DemoRequestText.phone(typed)).doesNotMatch(DemoRequestText.EGYPTIAN_MOBILE);
	}

	@Test
	void namesAreTrimmedAndEveryRunOfWhitespaceCollapses() {
		assertThat(DemoRequestText.collapse("  Nile \t Freight\n\r\nCo  ")).isEqualTo("Nile Freight Co");
		assertThat(DemoRequestText.collapse("  شركة   النيل  ")).isEqualTo("شركة النيل");
		assertThat(DemoRequestText.collapse(null)).isNull();
	}

	@Test
	void controlAndBidiOverrideCharactersAreNotPlainText() {
		assertThat("Nile Freight").matches(DemoRequestText.PLAIN_TEXT);
		assertThat("شركة النيل للنقل").matches(DemoRequestText.PLAIN_TEXT);
		assertThat("Nile\u0000Freight").doesNotMatch(DemoRequestText.PLAIN_TEXT);
		assertThat("Nile\u0007").doesNotMatch(DemoRequestText.PLAIN_TEXT);
		assertThat("Nile\u001B[31m").doesNotMatch(DemoRequestText.PLAIN_TEXT);
		assertThat("evil‮txt.exe").doesNotMatch(DemoRequestText.PLAIN_TEXT);
		assertThat("x⁦y").doesNotMatch(DemoRequestText.PLAIN_TEXT);
		// A note keeps its line breaks, and nothing else of the kind.
		assertThat("Called.\nCall back Sunday.\r\n\tPricing").matches(DemoRequestText.NOTE_TEXT);
		assertThat("Called\u0000").doesNotMatch(DemoRequestText.NOTE_TEXT);
		assertThat("Called‮").doesNotMatch(DemoRequestText.NOTE_TEXT);
	}

	@Test
	void emailsAreTrimmedAndLowerCased() {
		assertThat(DemoRequestText.email("  Mona.Adel+Fleet@Example.COM ")).isEqualTo("mona.adel+fleet@example.com");
	}

	@Test
	void aRequestIsSetAsideWhenTheHiddenFieldIsFilledOrTheTimingIsNotAPersons() {
		Instant now = Instant.parse("2026-11-20T10:00:00Z");
		assertThat(Leads.dropReason(submission(null, now.minusSeconds(10)), now)).isNull();
		assertThat(Leads.dropReason(submission("", now.minusSeconds(10)), now)).isNull();
		assertThat(Leads.dropReason(submission("   ", now.minusSeconds(10)), now)).isNull();
		assertThat(Leads.dropReason(submission("https://spam.example", now.minusSeconds(10)), now)).isEqualTo("honeypot");
		assertThat(Leads.dropReason(submission(null, now.minusMillis(2_999)), now)).isEqualTo("too_fast");
		assertThat(Leads.dropReason(submission(null, now), now)).isEqualTo("too_fast");
		assertThat(Leads.dropReason(submission(null, now.minusSeconds(3)), now)).isNull();
		assertThat(Leads.dropReason(submission(null, now.plusSeconds(1)), now)).isEqualTo("future");
		assertThat(Leads.dropReason(submission(null, now.minusSeconds(24 * 3600)), now)).isNull();
		assertThat(Leads.dropReason(submission(null, now.minusSeconds(24 * 3600 + 1)), now)).isEqualTo("stale");
		assertThat(Leads.dropReason(submission(null, Instant.ofEpochMilli(0)), now)).isEqualTo("stale");
	}

	private static Submission submission(String website, Instant startedAt) {
		return new Submission("Nile Freight", "Mona Adel", "01001234567", "mona@example.com", "20-49", "ar", website,
				startedAt.toEpochMilli());
	}

}
