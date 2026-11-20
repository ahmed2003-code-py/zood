package com.zawed.api.platform.text;

import static org.assertj.core.api.Assertions.assertThat;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * Egyptian phone numbers, tax registration numbers and commercial registrations (QA round 1): the
 * spellings people type are normalised to one stored form, and validation judges that form.
 */
class EgyptianContactTests {

	@ParameterizedTest
	@CsvSource(delimiter = '|', value = { "01001234567|01001234567", "+20 100 123 4567|01001234567",
			"0020-100-123-4567|01001234567", "201001234567|01001234567", "٠١٠٠١٢٣٤٥٦٧|01001234567",
			"015 5555 5555|01555555555" })
	void mobilesAreNormalisedAndAccepted(String typed, String stored) {
		assertThat(EgyptianContact.phone(typed)).isEqualTo(stored)
			.matches(EgyptianContact.MOBILE)
			.matches(EgyptianContact.PHONE);
	}

	@ParameterizedTest
	@CsvSource(delimiter = '|', value = { "02 2345 6789|0223456789", "(02) 2345-6789|0223456789",
			"+20 2 2345 6789|0223456789", "0020 2 23456789|0223456789", "03 456 7890|034567890",
			"+20 3 4567890|034567890", "040 333 4444|0403334444", "088 123 4567|0881234567",
			"013 222 3333|0132223333", "٠٢٢٣٤٥٦٧٨٩|0223456789" })
	void landlinesAreNormalisedAndAcceptedAsAPhoneButNotAsAMobile(String typed, String stored) {
		assertThat(EgyptianContact.phone(typed)).isEqualTo(stored)
			.matches(EgyptianContact.PHONE)
			.doesNotMatch(EgyptianContact.MOBILE);
	}

	@ParameterizedTest
	@ValueSource(strings = { "12345", "0100123456", "010012345678", "01301234567", "022345678", "02234567890",
			"0345678901", "+1 202 555 0100", "call me", "0223456789x" })
	void anythingElseIsRefused(String typed) {
		assertThat(EgyptianContact.phone(typed)).doesNotMatch(EgyptianContact.PHONE);
	}

	@ParameterizedTest
	@CsvSource(delimiter = '|', value = { "123456789|123456789", "123-456-789|123456789", " 123 456 789 |123456789",
			"١٢٣-٤٥٦-٧٨٩|123456789" })
	void taxIdsAreNineDigits(String typed, String stored) {
		assertThat(EgyptianContact.taxId(typed)).isEqualTo(stored).matches(EgyptianContact.TAX_ID);
	}

	@ParameterizedTest
	@ValueSource(strings = { "12345678", "1234567890", "TAX-12345", "123-456-78a" })
	void otherTaxIdsAreRefused(String typed) {
		assertThat(EgyptianContact.taxId(typed)).doesNotMatch(EgyptianContact.TAX_ID);
	}

	@ParameterizedTest
	@CsvSource(delimiter = '|', value = { "12345|12345", "١٢٣٤٥٦|123456", "123 456|123456", "2020-98765|202098765" })
	void commercialRegistrationsAreDigits(String typed, String stored) {
		assertThat(EgyptianContact.commercialRegistration(typed)).isEqualTo(stored)
			.matches(EgyptianContact.COMMERCIAL_REGISTRATION);
	}

	@ParameterizedTest
	@ValueSource(strings = { "CR-ABC", "12", "1234567890123" })
	void otherCommercialRegistrationsAreRefused(String typed) {
		assertThat(EgyptianContact.commercialRegistration(typed)).doesNotMatch(EgyptianContact.COMMERCIAL_REGISTRATION);
	}

	@Test
	void nullStaysNullAndAnOptionalBlankBecomesNull() {
		assertThat(EgyptianContact.phone(null)).isNull();
		assertThat(EgyptianContact.taxId(null)).isNull();
		assertThat(EgyptianContact.commercialRegistration(null)).isNull();
		assertThat(EgyptianContact.phone("   ")).isEmpty();
		assertThat(EgyptianContact.orNull(EgyptianContact.phone("   "))).isNull();
		assertThat(EgyptianContact.orNull(EgyptianContact.taxId(" - "))).isNull();
		assertThat(EgyptianContact.orNull("0223456789")).isEqualTo("0223456789");
	}

}
