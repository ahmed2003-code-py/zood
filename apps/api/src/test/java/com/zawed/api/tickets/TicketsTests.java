package com.zawed.api.tickets;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.UUID;

import com.jayway.jsonpath.JsonPath;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.platform.security.Role;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ScopeType;

/**
 * Support tickets: each side sees only its own, Operations answers and keeps internal notes to
 * itself, a ticket links only to records its author can see, and a closed ticket takes no replies.
 */
class TicketsTests extends IdentityTestSupport {

	private SignedIn ops;

	@BeforeEach
	void signInOps() throws Exception {
		ops = signIn(seed(Role.OPS));
	}

	@Test
	void aCompanyAsksAndOperationsAnswers() throws Exception {
		UUID company = seedCompany();
		SignedIn fleet = companyUser(company);
		SignedIn otherFleet = companyUser(seedCompany());

		HttpResponse<String> opened = authed("POST", "/api/v1/company/tickets", fleet.accessToken(),
				"{\"type\":\"LOST_TAG\",\"subject\":\"Tag lost on the road\",\"body\":\"The tag of car 12 fell off.\"}");
		assertThat(opened.statusCode()).as(opened.body()).isEqualTo(201);
		String id = JsonPath.read(opened.body(), "$.ticket.id");
		assertThat((String) JsonPath.read(opened.body(), "$.ticket.reference")).startsWith("T-");
		assertThat((String) JsonPath.read(opened.body(), "$.ticket.status")).isEqualTo("OPEN");

		// Another company neither lists nor reads it.
		assertThat(authed("GET", "/api/v1/company/tickets/" + id, otherFleet.accessToken(), null).statusCode())
			.isEqualTo(404);
		List<String> theirs = JsonPath.read(authed("GET", "/api/v1/company/tickets", otherFleet.accessToken(), null).body(),
				"$.items[*].id");
		assertThat(theirs).doesNotContain(id);

		// Operations answers (which takes the ticket up) and adds a note the company never sees.
		HttpResponse<String> answer = authed("POST", "/api/v1/ops/tickets/" + id + "/messages", ops.accessToken(),
				"{\"body\":\"A new tag is on its way.\"}");
		assertThat(answer.statusCode()).as(answer.body()).isEqualTo(200);
		assertThat((String) JsonPath.read(answer.body(), "$.ticket.status")).isEqualTo("IN_PROGRESS");
		authed("POST", "/api/v1/ops/tickets/" + id + "/messages", ops.accessToken(),
				"{\"body\":\"Check the tag stock first\",\"internal\":true}");

		HttpResponse<String> seenByCompany = authed("GET", "/api/v1/company/tickets/" + id, fleet.accessToken(), null);
		List<Boolean> internal = JsonPath.read(seenByCompany.body(), "$.messages[*].internal");
		assertThat(internal).hasSize(2).containsOnly(false);
		List<Boolean> seenByOps = JsonPath.read(authed("GET", "/api/v1/ops/tickets/" + id, ops.accessToken(), null).body(),
				"$.messages[*].internal");
		assertThat(seenByOps).hasSize(3).contains(true);

		// Resolved and then closed: no more replies.
		HttpResponse<String> closed = authed("POST", "/api/v1/ops/tickets/" + id + "/status", ops.accessToken(),
				"{\"status\":\"CLOSED\"}");
		assertThat((String) JsonPath.read(closed.body(), "$.ticket.status")).isEqualTo("CLOSED");
		assertThat((String) JsonPath.read(closed.body(), "$.ticket.resolvedAt")).isNotNull();
		HttpResponse<String> late = authed("POST", "/api/v1/company/tickets/" + id + "/messages", fleet.accessToken(),
				"{\"body\":\"One more thing\"}");
		assertThat(code(late)).isEqualTo("TICKET_CLOSED");
	}

	@Test
	void eachSideAsksOnlyWhatItMayAsk() throws Exception {
		SignedIn fleet = companyUser(seedCompany());
		HttpResponse<String> wrong = authed("POST", "/api/v1/company/tickets", fleet.accessToken(),
				"{\"type\":\"DEVICE_FAULT\",\"subject\":\"x\",\"body\":\"y\"}");
		assertThat(code(wrong)).isEqualTo("TICKET_TYPE_NOT_ALLOWED");

		String operator = operator();
		SignedIn manager = signIn(seed(Role.STATION_MANAGER, new Scope(ScopeType.OPERATOR, UUID.fromString(operator)), false));
		HttpResponse<String> fault = authed("POST", "/api/v1/station/tickets", manager.accessToken(),
				"{\"type\":\"DEVICE_FAULT\",\"subject\":\"Reader dead\",\"body\":\"Pump 3 does not read tags.\"}");
		assertThat(fault.statusCode()).as(fault.body()).isEqualTo(201);
		assertThat((String) JsonPath.read(fault.body(), "$.ticket.operatorId")).isEqualTo(operator);
		List<String> listed = JsonPath.read(authed("GET", "/api/v1/station/tickets", manager.accessToken(), null).body(),
				"$.items[*].id");
		assertThat(listed).containsExactly((String) JsonPath.read(fault.body(), "$.ticket.id"));
	}

	@Test
	void aTicketLinksOnlyToRecordsItsAuthorCanSee() throws Exception {
		UUID mine = seedCompany();
		String theirVehicle = vehicle(seedCompany());
		SignedIn fleet = companyUser(mine);

		HttpResponse<String> foreign = authed("POST", "/api/v1/company/tickets", fleet.accessToken(),
				"{\"type\":\"VEHICLE_CHANGE\",\"subject\":\"x\",\"body\":\"y\",\"vehicleId\":\"" + theirVehicle + "\"}");
		assertThat(code(foreign)).isEqualTo("TICKET_LINK_NOT_FOUND");

		String myVehicle = vehicle(mine);
		HttpResponse<String> own = authed("POST", "/api/v1/company/tickets", fleet.accessToken(),
				"{\"type\":\"VEHICLE_CHANGE\",\"subject\":\"New plate\",\"body\":\"Plate changed\",\"vehicleId\":\"" + myVehicle
						+ "\"}");
		assertThat(own.statusCode()).as(own.body()).isEqualTo(201);
	}

	private SignedIn companyUser(UUID company) throws Exception {
		return signIn(seed(Role.FLEET_MANAGER, new Scope(ScopeType.COMPANY, company), false));
	}

	private String operator() throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/operators", ops.accessToken(),
				"{\"legalName\":\"Op " + UUID.randomUUID() + "\",\"nameAr\":\"مشغل\",\"nameEn\":\"Operator\"}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

	private String vehicle(UUID company) throws Exception {
		HttpResponse<String> created = authed("POST", "/api/v1/ops/vehicles", ops.accessToken(),
				"{\"companyId\":\"" + company + "\",\"vehicle\":{\"plate\":\"TK " + UUID.randomUUID().toString().substring(0, 8)
					.toUpperCase() + "\",\"make\":\"Toyota\",\"model\":\"Hilux\",\"fuelType\":\"GAS_92\",\"tankCapacityMl\":60000,"
						+ "\"tier\":\"NORMAL\"}}");
		assertThat(created.statusCode()).as(created.body()).isEqualTo(201);
		return JsonPath.read(created.body(), "$.id");
	}

}
