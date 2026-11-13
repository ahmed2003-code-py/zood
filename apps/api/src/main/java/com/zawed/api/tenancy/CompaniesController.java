package com.zawed.api.tenancy;

import java.time.Instant;
import java.util.UUID;

import jakarta.validation.Valid;
import jakarta.validation.constraints.Email;
import jakarta.validation.constraints.Max;
import jakarta.validation.constraints.Min;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.PositiveOrZero;
import jakarta.validation.constraints.Size;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.PutMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;
import com.zawed.api.tenancy.Companies.Company;
import com.zawed.api.tenancy.Companies.Department;
import com.zawed.api.tenancy.Companies.Status;

/** Company onboarding and management in the Ops console (PRD §6.3). */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/ops/companies")
@Tag(name = "companies")
class CompaniesController {

	record CompanyBody(@NotBlank @Size(max = 200) String legalName, @NotBlank @Size(max = 200) String nameAr,
			@NotBlank @Size(max = 200) String nameEn, @Size(max = 50) String taxId,
			@Size(max = 50) String commercialRegistration, @Size(max = 200) String contactName,
			@Size(max = 30) String contactPhone, @Email @Size(max = 254) String contactEmail) {

		Companies.Draft draft() {
			return new Companies.Draft(legalName, nameAr, nameEn, taxId, commercialRegistration, contactName,
					contactPhone, contactEmail);
		}

	}

	record CreateCompanyRequest(@NotNull @Valid CompanyBody company,
			@Min(120) @Max(240) @Schema(description = "Defaults to 180") Integer cooldownMinutes,
			@NotNull @PositiveOrZero Long lowBalanceThresholdPiastres) {
	}

	record CompanyPolicyRequest(@NotNull @Min(120) @Max(240) Integer cooldownMinutes,
			@NotNull @PositiveOrZero Long lowBalanceThresholdPiastres, @NotBlank @Size(max = 500) String reason) {
	}

	record CompanyStatusRequest(@NotNull Status status, @NotBlank @Size(max = 500) String reason) {
	}

	record CreateDepartmentRequest(@NotBlank @Size(max = 120) String name, @PositiveOrZero Long monthlyBudgetPiastres) {
	}

	record CompanyResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String legalName,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameAr,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameEn, String taxId, String commercialRegistration,
			String contactName, String contactPhone, String contactEmail,
			@Schema(requiredMode = RequiredMode.REQUIRED) Status status,
			@Schema(requiredMode = RequiredMode.REQUIRED) int cooldownMinutes,
			@Schema(requiredMode = RequiredMode.REQUIRED) long lowBalanceThresholdPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) Instant createdAt) {

		static CompanyResponse of(Company c) {
			return new CompanyResponse(c.id(), c.legalName(), c.nameAr(), c.nameEn(), c.taxId(),
					c.commercialRegistration(), c.contactName(), c.contactPhone(), c.contactEmail(), c.status(),
					c.cooldownMinutes(), c.lowBalanceThresholdPiastres(), c.createdAt());
		}

	}

	record DepartmentResponse(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String name, Long monthlyBudgetPiastres,
			@Schema(requiredMode = RequiredMode.REQUIRED) boolean active) {

		static DepartmentResponse of(Department d) {
			return new DepartmentResponse(d.id(), d.name(), d.monthlyBudgetPiastres(), d.active());
		}

	}

	private final Companies companies;

	CompaniesController(Companies companies) {
		this.companies = companies;
	}

	@GetMapping
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listCompanies", summary = "Companies, newest first")
	Page<CompanyResponse> list(@RequestParam(required = false) Status status, @RequestParam(required = false) String q,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<Company> page = companies.list(status, q, cursor, limit);
		return new Page<>(page.items().stream().map(CompanyResponse::of).toList(), page.nextCursor());
	}

	@PostMapping
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.COMPANIES_MANAGE)
	@Operation(operationId = "createCompany", summary = "Onboard a company")
	CompanyResponse create(@AuthenticationPrincipal ZawedPrincipal actor, @Valid @RequestBody CreateCompanyRequest body) {
		return CompanyResponse.of(companies.create(actor, body.company().draft(), body.cooldownMinutes(),
				body.lowBalanceThresholdPiastres()));
	}

	@GetMapping("/{id}")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "getCompany", summary = "One company")
	CompanyResponse get(@PathVariable UUID id) {
		return CompanyResponse.of(companies.get(id));
	}

	@PutMapping("/{id}")
	@RequiresPermission(Permission.COMPANIES_MANAGE)
	@Operation(operationId = "updateCompany", summary = "Edit a company's identity and contacts")
	CompanyResponse update(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody CompanyBody body) {
		return CompanyResponse.of(companies.update(actor, id, body.draft()));
	}

	@PutMapping("/{id}/policy")
	@RequiresPermission(Permission.COMPANY_POLICY_MANAGE)
	@Operation(operationId = "updateCompanyPolicy", summary = "Cooldown window and low-balance threshold (Super Admin)")
	CompanyResponse policy(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody CompanyPolicyRequest body) {
		return CompanyResponse.of(companies.updatePolicy(actor, id, body.cooldownMinutes(),
				body.lowBalanceThresholdPiastres(), body.reason().trim()));
	}

	@PostMapping("/{id}/status")
	@RequiresPermission(Permission.COMPANIES_MANAGE)
	@Operation(operationId = "setCompanyStatus", summary = "Freeze or unfreeze a company")
	CompanyResponse status(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody CompanyStatusRequest body) {
		return CompanyResponse.of(companies.setStatus(actor, id, body.status(), body.reason().trim()));
	}

	@GetMapping("/{id}/departments")
	@RequiresPermission(Permission.OPS_READ)
	@Operation(operationId = "listCompanyDepartments", summary = "A company's departments")
	Page<DepartmentResponse> departments(@PathVariable UUID id, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<Department> page = companies.departments(id, cursor, limit);
		return new Page<>(page.items().stream().map(DepartmentResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/{id}/departments")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.COMPANIES_MANAGE)
	@Operation(operationId = "createDepartment", summary = "Add a department")
	DepartmentResponse addDepartment(@AuthenticationPrincipal ZawedPrincipal actor, @PathVariable UUID id,
			@Valid @RequestBody CreateDepartmentRequest body) {
		return DepartmentResponse.of(companies.addDepartment(actor, id, body.name(), body.monthlyBudgetPiastres()));
	}

}
