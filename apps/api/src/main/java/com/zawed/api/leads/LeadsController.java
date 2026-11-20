package com.zawed.api.leads;

import java.time.Instant;
import java.util.UUID;

import jakarta.validation.Valid;
import jakarta.validation.constraints.Email;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Pattern;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PatchMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.leads.Leads.Lead;
import com.zawed.api.leads.Leads.Status;
import com.zawed.api.leads.Leads.Submission;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/**
 * Demo requests from the public landing page, and the Ops list they land in (U1,
 * u1-leads-contract.md). The public half needs no identity; it is protected by {@link DemoRequestGate}
 * (shared per-address limits, a body cap) and by {@link Leads} (honeypot, time trap, per-contact
 * limits, repeats).
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH)
class LeadsController {

	/**
	 * What the form sends. The compact constructor cleans the text first (trim, collapse whitespace,
	 * normalise the phone, lower-case the e-mail); validation then judges the cleaned values.
	 */
	@Schema(description = "A demo request from the landing page")
	record DemoRequest(
			@Schema(requiredMode = RequiredMode.REQUIRED, minLength = 2, maxLength = 120) @NotBlank @Size(min = 2,
					max = 120) @Pattern(regexp = DemoRequestText.PLAIN_TEXT) String companyName,
			@Schema(requiredMode = RequiredMode.REQUIRED, minLength = 2, maxLength = 80) @NotBlank @Size(min = 2,
					max = 80) @Pattern(regexp = DemoRequestText.PLAIN_TEXT) String contactName,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					description = "An Egyptian mobile number; +20, 0020, spaces, dashes and Arabic digits are accepted "
							+ "and stored as 01XXXXXXXXX") @NotBlank @Pattern(
									regexp = DemoRequestText.EGYPTIAN_MOBILE) String phone,
			@Schema(requiredMode = RequiredMode.REQUIRED, maxLength = 254) @NotBlank @Size(max = 254) @Email(
					regexp = "[^@\\s]+@[^@\\s]+\\.[^@\\s]+") @Pattern(regexp = DemoRequestText.PLAIN_TEXT) String email,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					allowableValues = { "1-19", "20-49", "50-99", "100-249", "250+" }) @NotNull @Pattern(
							regexp = "1-19|20-49|50-99|100-249|250\\+") String fleetSize,
			@Schema(requiredMode = RequiredMode.REQUIRED, allowableValues = { "ar", "en" }) @NotNull @Pattern(
					regexp = "ar|en") String locale,
			@Schema(description = "Leave empty. A field people never see; a request that fills it is not stored") String website,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					description = "When the form was shown, epoch milliseconds") @NotNull Long startedAt) {

		DemoRequest {
			companyName = DemoRequestText.collapse(companyName);
			contactName = DemoRequestText.collapse(contactName);
			phone = DemoRequestText.phone(phone);
			email = DemoRequestText.email(email);
		}

	}

	@Schema(description = "Always the same answer, stored or not: the form never learns what happened to it")
	record DemoRequestReceived(@Schema(requiredMode = RequiredMode.REQUIRED, allowableValues = "RECEIVED") String status) {

		static final DemoRequestReceived RECEIVED = new DemoRequestReceived("RECEIVED");

	}

	record LeadResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED, example = "L-10001") String reference,
			@Schema(requiredMode = RequiredMode.REQUIRED) String companyName,
			@Schema(requiredMode = RequiredMode.REQUIRED) String contactName,
			@Schema(requiredMode = RequiredMode.REQUIRED, example = "01001234567") String phone,
			@Schema(requiredMode = RequiredMode.REQUIRED) String email,
			@Schema(requiredMode = RequiredMode.REQUIRED,
					allowableValues = { "1-19", "20-49", "50-99", "100-249", "250+" }) String fleetSize,
			@Schema(requiredMode = RequiredMode.REQUIRED, allowableValues = { "ar", "en" }) String locale,
			@Schema(requiredMode = RequiredMode.REQUIRED) Status status, String note,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant updatedAt) {

		static LeadResponse of(Lead lead) {
			return new LeadResponse(lead.id(), lead.reference(), lead.companyName(), lead.contactName(), lead.phone(),
					lead.email(), lead.fleetSize(), lead.locale(), lead.status(), lead.note(), lead.createdAt(),
					lead.updatedAt());
		}

	}

	record LeadUpdateRequest(@NotNull Status status,
			@Schema(description = "Omitted: unchanged. Blank: removed. Line breaks are kept", maxLength = 1000) @Size(
					max = 1000) @Pattern(regexp = DemoRequestText.NOTE_TEXT) String note) {
	}

	private final Leads leads;

	LeadsController(Leads leads) {
		this.leads = leads;
	}

	@PostMapping("/public/demo-requests")
	@ResponseStatus(HttpStatus.ACCEPTED)
	@Tag(name = "public")
	@Operation(operationId = "requestDemo", summary = "Ask for a demo from the landing page",
			description = "No sign-in. Limited per address (5 an hour, 20 a day) and per phone and e-mail (3 a day "
					+ "each); 429 RATE_LIMITED with Retry-After past that. A body over 4 KiB is refused (413). The answer "
					+ "is 202 RECEIVED whether the request became a new lead, counted on an existing one, or was set aside.")
	DemoRequestReceived requestDemo(@Valid @RequestBody DemoRequest body) {
		leads.submit(new Submission(body.companyName(), body.contactName(), body.phone(), body.email(), body.fleetSize(),
				body.locale(), body.website(), body.startedAt()));
		return DemoRequestReceived.RECEIVED;
	}

	@GetMapping("/ops/leads")
	@Tag(name = "leads")
	@RequiresPermission(Permission.LEADS_MANAGE)
	@Operation(operationId = "listLeads", summary = "Demo requests, newest first")
	Page<LeadResponse> list(@RequestParam(required = false) Status status, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<Lead> page = leads.list(status, cursor, limit);
		return new Page<>(page.items().stream().map(LeadResponse::of).toList(), page.nextCursor());
	}

	@PatchMapping("/ops/leads/{id}")
	@Tag(name = "leads")
	@RequiresPermission(Permission.LEADS_MANAGE)
	@Operation(operationId = "updateLead", summary = "Move a lead along and keep a note on it")
	LeadResponse update(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id,
			@Valid @RequestBody LeadUpdateRequest body) {
		return LeadResponse.of(leads.update(principal, id, body.status(), body.note()));
	}

}
