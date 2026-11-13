package com.zawed.api.tenancy;

import java.util.UUID;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.media.Schema;
import io.swagger.v3.oas.annotations.media.Schema.RequiredMode;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;
import com.zawed.api.tenancy.Companies.Company;
import com.zawed.api.tenancy.CompaniesController.DepartmentResponse;

/**
 * The company portal's view of its own company. The company id always comes from the signed-in
 * user's scope, never from the request, and row-level security would hide any other company anyway.
 * Read-only: changes go through requests to Operations.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/company")
@Tag(name = "company portal")
class CompanyPortalController {

	/** What a company sees about itself: no internal notes, no other company's data. */
	record CompanyProfile(@Schema(requiredMode = RequiredMode.REQUIRED) UUID id,
			@Schema(requiredMode = RequiredMode.REQUIRED) String legalName,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameAr,
			@Schema(requiredMode = RequiredMode.REQUIRED) String nameEn,
			@Schema(requiredMode = RequiredMode.REQUIRED) Companies.Status status,
			@Schema(requiredMode = RequiredMode.REQUIRED) int cooldownMinutes,
			@Schema(requiredMode = RequiredMode.REQUIRED) long lowBalanceThresholdPiastres) {

		static CompanyProfile of(Company c) {
			return new CompanyProfile(c.id(), c.legalName(), c.nameAr(), c.nameEn(), c.status(), c.cooldownMinutes(),
					c.lowBalanceThresholdPiastres());
		}

	}

	private final Companies companies;

	CompanyPortalController(Companies companies) {
		this.companies = companies;
	}

	@GetMapping("/profile")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "getMyCompany", summary = "The signed-in user's company")
	CompanyProfile profile(@AuthenticationPrincipal ZawedPrincipal principal) {
		return CompanyProfile.of(companies.get(principal.scope().id()));
	}

	@GetMapping("/departments")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "listMyDepartments", summary = "The company's departments")
	Page<DepartmentResponse> departments(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<Companies.Department> page = companies.departments(principal.scope().id(), cursor, limit);
		return new Page<>(page.items().stream().map(DepartmentResponse::of).toList(), page.nextCursor());
	}

}
