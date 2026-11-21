package com.zawed.api.tenancy;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.support.Owner;

/**
 * A company's and an operator's contact phone, tax registration number and commercial registration
 * are validated and stored in one form (QA round 1): {@code +20 2 2345 6789} is stored as
 * {@code 0223456789} and {@code 123-456-789} as {@code 123456789}. Rows written before the rule
 * stay readable as they are.
 */
class CompanyContactTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void contactDetailsAreNormalisedOnCreateAndUpdate() throws Exception {
		String digits = taxDigits();
		String typedTaxId = digits.substring(0, 3) + "-" + digits.substring(3, 6) + "-" + digits.substring(6);
		HttpResponse<String> created = createCompany("\"taxId\":\"" + typedTaxId
				+ "\",\"commercialRegistration\":\"١٢٣٤٥\",\"contactPhone\":\"+20 2 2345 6789\"");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(created.body(), "$.taxId")).isEqualTo(digits);
		assertThat(JsonPath.<String>read(created.body(), "$.commercialRegistration")).isEqualTo("12345");
		assertThat(JsonPath.<String>read(created.body(), "$.contactPhone")).isEqualTo("0223456789");
		String id = JsonPath.read(created.body(), "$.id");

		HttpResponse<String> updated = authed("PUT", "/api/v1/ops/companies/" + id, ops.accessToken(),
				"{\"legalName\":\"Contact Co\",\"nameAr\":\"شركة\",\"nameEn\":\"Contact Co\",\"taxId\":\"" + digits
						+ "\",\"contactPhone\":\"0020 100 123 4567\",\"commercialRegistration\":\"  \"}");
		assertThat(updated.statusCode()).as(updated.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(updated.body(), "$.contactPhone")).isEqualTo("01001234567");
		assertThat(JsonPath.<Object>read(updated.body(), "$.commercialRegistration")).as("blank is no value").isNull();

		// The same tax id typed with dashes is the same company.
		HttpResponse<String> duplicate = createCompany("\"taxId\":\"" + typedTaxId + "\"");
		assertThat(code(duplicate)).isEqualTo("TAX_ID_IN_USE");

		// Ops finds it by the tax id however it is typed.
		List<String> found = JsonPath.read(authed("GET", "/api/v1/ops/companies?q=" + typedTaxId, ops.accessToken(), null)
			.body(), "$.items[*].id");
		assertThat(found).containsExactly(id);
	}

	@Test
	void invalidContactDetailsAreRefusedByField() throws Exception {
		assertInvalid(createCompany("\"contactPhone\":\"12345\""), "company.contactPhone");
		assertInvalid(createCompany("\"contactPhone\":\"01301234567\""), "company.contactPhone");
		assertInvalid(createCompany("\"contactPhone\":\"call me\""), "company.contactPhone");
		assertInvalid(createCompany("\"taxId\":\"TAX-123\""), "company.taxId");
		assertInvalid(createCompany("\"taxId\":\"12345678\""), "company.taxId");
		assertInvalid(createCompany("\"commercialRegistration\":\"CR/ABC\""), "company.commercialRegistration");

		assertInvalid(authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op\",\"nameAr\":\"م\",\"nameEn\":\"Op\",\"contactPhone\":\"5551234\"}"), "contactPhone");
		assertInvalid(authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op\",\"nameAr\":\"م\",\"nameEn\":\"Op\",\"taxId\":\"x\"}"), "taxId");
	}

	@Test
	void anOperatorsContactDetailsAreNormalisedToo() throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op " + UUID.randomUUID() + "\",\"nameAr\":\"م\",\"nameEn\":\"Op\",\"taxId\":\"" + taxDigits()
						.replaceFirst("(\\d{3})(\\d{3})", "$1-$2-")
						+ "\",\"contactPhone\":\"+20 3 4567890\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		assertThat(JsonPath.<String>read(created.body(), "$.contactPhone")).isEqualTo("034567890");
		assertThat(JsonPath.<String>read(created.body(), "$.taxId")).matches("[0-9]{9}");
	}

	@Test
	void rowsWrittenBeforeTheRuleStayReadable() throws Exception {
		UUID legacy = UUID.randomUUID();
		Owner.update("""
				insert into companies (id, legal_name, name_ar, name_en, tax_id, commercial_registration, contact_phone,
				                       status, cooldown_minutes, low_balance_threshold_piastres, created_at, updated_at)
				values (?, 'Legacy', 'قديمة', 'Legacy', ?, 'CR old/7', 'call the office', 'ACTIVE', 180, 0, now(), now())
				""", legacy, "TAX-" + legacy.toString().substring(0, 8));

		HttpResponse<String> read = authed("GET", "/api/v1/ops/companies/" + legacy, ops.accessToken(), null);
		assertThat(read.statusCode()).as(read.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(read.body(), "$.contactPhone")).isEqualTo("call the office");
		assertThat(JsonPath.<String>read(read.body(), "$.commercialRegistration")).isEqualTo("CR old/7");
	}

	/**
	 * QA round 1 review: a row written before the rule may hold 123-456-789 or Arabic-Indic digits.
	 * The unique index compares tax_id_key(tax_id) since V23, so any spelling of the same number is
	 * the same tax id, for companies and operators.
	 */
	@Test
	void aLegacySpellingOfATaxIdStillBlocksTheSameNumber() throws Exception {
		String digits = taxDigits();
		String arabicDashed = toArabic(digits.substring(0, 3) + "-" + digits.substring(3, 6) + "-" + digits.substring(6));
		UUID legacy = UUID.randomUUID();
		Owner.update("""
				insert into companies (id, legal_name, name_ar, name_en, tax_id, status, cooldown_minutes,
				                       low_balance_threshold_piastres, created_at, updated_at)
				values (?, 'Legacy Tax', 'قديمة', 'Legacy Tax', ?, 'ACTIVE', 180, 0, now(), now())
				""", legacy, arabicDashed);

		assertThat(code(createCompany("\"taxId\":\"" + digits + "\""))).isEqualTo("TAX_ID_IN_USE");
		for (String query : new String[] { digits, digits.substring(0, 3) + "-" + digits.substring(3, 6) + "-"
				+ digits.substring(6) }) {
			List<String> found = JsonPath.read(authed("GET", "/api/v1/ops/companies?q=" + query, ops.accessToken(), null)
				.body(), "$.items[*].id");
			assertThat(found).as(query).containsExactly(legacy.toString());
		}

		String operatorDigits = taxDigits();
		Owner.update("""
				insert into station_operators (id, legal_name, name_ar, name_en, tax_id, status, created_at, updated_at)
				values (?, 'Legacy Op', 'م', 'Legacy Op', ?, 'ACTIVE', now(), now())
				""", UUID.randomUUID(), operatorDigits.substring(0, 3) + " " + operatorDigits.substring(3));
		assertThat(code(authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op\",\"nameAr\":\"م\",\"nameEn\":\"Op\",\"taxId\":\"" + operatorDigits + "\"}")))
			.isEqualTo("TAX_ID_IN_USE");
	}

	/**
	 * A full-body PUT sends back what it read. A legacy value that was never valid is accepted while it
	 * is unchanged; only a field that changes is judged by the new rules.
	 */
	@Test
	void anUnchangedLegacyValueIsAcceptedOnUpdate() throws Exception {
		UUID legacy = UUID.randomUUID();
		String legacyTax = "TAX-" + legacy.toString().substring(0, 8);
		Owner.update("""
				insert into companies (id, legal_name, name_ar, name_en, tax_id, commercial_registration, contact_phone,
				                       status, cooldown_minutes, low_balance_threshold_piastres, created_at, updated_at)
				values (?, 'Legacy', 'قديمة', 'Legacy', ?, 'CR old/7', 'call the office', 'ACTIVE', 180, 0, now(), now())
				""", legacy, legacyTax);
		String unchanged = "\"taxId\":\"" + legacyTax + "\",\"commercialRegistration\":\"CR old/7\","
				+ "\"contactPhone\":\"call the office\"";

		HttpResponse<String> renamed = authed("PUT", "/api/v1/ops/companies/" + legacy, ops.accessToken(),
				"{\"legalName\":\"Legacy Renamed\",\"nameAr\":\"قديمة\",\"nameEn\":\"Legacy\"," + unchanged + "}");
		assertThat(renamed.statusCode()).as(renamed.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(renamed.body(), "$.legalName")).isEqualTo("Legacy Renamed");
		assertThat(JsonPath.<String>read(renamed.body(), "$.contactPhone")).isEqualTo("call the office");
		assertThat(JsonPath.<String>read(renamed.body(), "$.taxId")).isEqualTo(legacyTax);

		// A changed field is judged; the unchanged ones still pass.
		HttpResponse<String> changed = authed("PUT", "/api/v1/ops/companies/" + legacy, ops.accessToken(),
				"{\"legalName\":\"Legacy\",\"nameAr\":\"قديمة\",\"nameEn\":\"Legacy\",\"taxId\":\"" + legacyTax
						+ "\",\"commercialRegistration\":\"CR old/7\",\"contactPhone\":\"12345\"}");
		assertInvalid(changed, "contactPhone");
		HttpResponse<String> fixed = authed("PUT", "/api/v1/ops/companies/" + legacy, ops.accessToken(),
				"{\"legalName\":\"Legacy\",\"nameAr\":\"قديمة\",\"nameEn\":\"Legacy\",\"taxId\":\"" + legacyTax
						+ "\",\"contactPhone\":\"+20 2 2345 6789\"}");
		assertThat(fixed.statusCode()).as(fixed.body()).isEqualTo(200);
		assertThat(JsonPath.<String>read(fixed.body(), "$.contactPhone")).isEqualTo("0223456789");
		assertThat(JsonPath.<Object>read(fixed.body(), "$.commercialRegistration")).as("omitted is cleared").isNull();
	}

	private static String toArabic(String value) {
		StringBuilder out = new StringBuilder();
		value.chars().forEach((c) -> out.append(c >= '0' && c <= '9' ? (char) ('٠' + (c - '0')) : (char) c));
		return out.toString();
	}

	private HttpResponse<String> createCompany(String fields) throws Exception {
		return authed("POST", "/api/v1/ops/companies", ops.accessToken(),
				"{\"company\":{\"legalName\":\"Contact Co\",\"nameAr\":\"شركة\",\"nameEn\":\"Contact Co\"," + fields
						+ "},\"lowBalanceThresholdPiastres\":0}");
	}

	private static void assertInvalid(HttpResponse<String> response, String field) {
		assertThat(response.statusCode()).as(response.body()).isEqualTo(400);
		assertThat(JsonPath.<String>read(response.body(), "$.code")).isEqualTo("VALIDATION_FAILED");
		assertThat(JsonPath.<List<String>>read(response.body(), "$.fields[*].field")).containsExactly(field);
	}

	static String taxDigits() {
		return String.format("%09d", ThreadLocalRandom.current().nextInt(1_000_000_000));
	}

}
