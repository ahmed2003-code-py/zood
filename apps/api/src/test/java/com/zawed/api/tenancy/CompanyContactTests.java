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
