package com.zawed.api.tenancy;

import static com.zawed.api.platform.persistence.Sql.clean;
import static com.zawed.api.platform.persistence.Sql.instant;
import static com.zawed.api.platform.persistence.Sql.nullableLong;
import static com.zawed.api.platform.persistence.Sql.ts;

import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Clock;
import java.time.Instant;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

import org.springframework.context.ApplicationEventPublisher;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpStatus;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

import com.zawed.api.audit.AuditLog;
import com.zawed.api.platform.ids.IdGenerator;
import com.zawed.api.platform.security.Scope;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiException;
import com.zawed.api.platform.web.Page;

/**
 * Companies and their departments (PRD §6.3 Onboard Company). Ops onboards and edits; the company
 * itself only reads (approved decision: company data is view-only, changes go through requests).
 * Every read and write runs under row-level security; every change is audited.
 */
@Service
class Companies implements CompanyDirectory {

	enum Status {

		ACTIVE, FROZEN

	}

	record Company(UUID id, String legalName, String nameAr, String nameEn, String taxId, String commercialRegistration,
			String contactName, String contactPhone, String contactEmail, Status status, int cooldownMinutes,
			long lowBalanceThresholdPiastres, Instant createdAt) {

		static Company map(ResultSet rs, int row) throws SQLException {
			return new Company(rs.getObject("id", UUID.class), rs.getString("legal_name"), rs.getString("name_ar"),
					rs.getString("name_en"), rs.getString("tax_id"), rs.getString("commercial_registration"),
					rs.getString("contact_name"), rs.getString("contact_phone"), rs.getString("contact_email"),
					Status.valueOf(rs.getString("status")), rs.getInt("cooldown_minutes"),
					rs.getLong("low_balance_threshold_piastres"), instant(rs, "created_at"));
		}

	}

	record Department(UUID id, UUID companyId, String name, Long monthlyBudgetPiastres, boolean active) {

		static Department map(ResultSet rs, int row) throws SQLException {
			return new Department(rs.getObject("id", UUID.class), rs.getObject("company_id", UUID.class),
					rs.getString("name"), nullableLong(rs, "monthly_budget_piastres"),
					"ACTIVE".equals(rs.getString("status")));
		}

	}

	record Draft(String legalName, String nameAr, String nameEn, String taxId, String commercialRegistration,
			String contactName, String contactPhone, String contactEmail) {
	}

	/** Default cooling-off: the middle of PRD's 2–4 hour range. */
	static final int DEFAULT_COOLDOWN_MINUTES = 180;

	private final JdbcClient jdbc;
	private final AuditLog audit;
	private final IdGenerator ids;
	private final Clock clock;
	private final ApplicationEventPublisher events;

	Companies(JdbcClient jdbc, AuditLog audit, IdGenerator ids, Clock clock, ApplicationEventPublisher events) {
		this.jdbc = jdbc;
		this.audit = audit;
		this.ids = ids;
		this.clock = clock;
		this.events = events;
	}

	// --------------------------------------------------------------------------------- directory

	@Override
	@Transactional(readOnly = true)
	public Optional<CompanyRef> find(UUID companyId) {
		return jdbc.sql("""
				select id, name_ar, name_en, status, cooldown_minutes, low_balance_threshold_piastres
				from companies where id = :id
				""")
			.param("id", companyId)
			.query((rs, n) -> new CompanyRef(rs.getObject("id", UUID.class), rs.getString("name_ar"),
					rs.getString("name_en"), "ACTIVE".equals(rs.getString("status")), rs.getInt("cooldown_minutes"),
					rs.getLong("low_balance_threshold_piastres")))
			.optional();
	}

	@Override
	@Transactional(readOnly = true)
	public boolean hasActiveDepartment(UUID companyId, UUID departmentId) {
		return Boolean.TRUE.equals(jdbc.sql("""
				select exists (select 1 from departments where id = :id and company_id = :company and status = 'ACTIVE')
				""").param("id", departmentId).param("company", companyId).query(Boolean.class).single());
	}

	// ----------------------------------------------------------------------------------- queries

	@Transactional(readOnly = true)
	Page<Company> list(Status status, String query, String cursor, Integer limit) {
		int size = Page.limit(limit);
		List<Company> rows = jdbc.sql("""
				select * from companies
				where (cast(:status as text) is null or status = :status)
				  and (cast(:q as text) is null or lower(legal_name) like :q or lower(name_ar) like :q
				       or lower(name_en) like :q or tax_id = :raw)
				  and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("status", status == null ? null : status.name())
			.param("q", like(query))
			.param("raw", clean(query))
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Company::map)
			.list();
		return Page.of(rows, size, Company::id);
	}

	@Transactional(readOnly = true)
	Company get(UUID id) {
		return jdbc.sql("select * from companies where id = :id")
			.param("id", id)
			.query(Company::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Company"));
	}

	/** Reads the company and holds its row until commit, so two changes never both read the same "before". */
	private Company lock(UUID id) {
		return jdbc.sql("select * from companies where id = :id for update")
			.param("id", id)
			.query(Company::map)
			.optional()
			.orElseThrow(() -> ApiException.notFound("Company"));
	}

	@Transactional(readOnly = true)
	Page<Department> departments(UUID companyId, String cursor, Integer limit) {
		get(companyId); // 404 for a company outside the caller's scope
		int size = Page.limit(limit);
		List<Department> rows = jdbc.sql("""
				select * from departments
				where company_id = :company and (cast(:cursor as uuid) is null or id < :cursor)
				order by id desc limit :limit
				""")
			.param("company", companyId)
			.param("cursor", Page.cursor(cursor))
			.param("limit", size + 1)
			.query(Department::map)
			.list();
		return Page.of(rows, size, Department::id);
	}

	// ----------------------------------------------------------------------------------- changes

	@Transactional
	Company create(ZawedPrincipal actor, Draft draft, Integer cooldownMinutes, long lowBalanceThresholdPiastres) {
		UUID id = ids.next();
		Instant now = clock.instant();
		int cooldown = cooldownMinutes == null ? DEFAULT_COOLDOWN_MINUTES : cooldownMinutes;
		try {
			jdbc.sql("""
					insert into companies (id, legal_name, name_ar, name_en, tax_id, commercial_registration, contact_name,
					                       contact_phone, contact_email, status, cooldown_minutes,
					                       low_balance_threshold_piastres, created_at, created_by, updated_at, updated_by)
					values (:id, :legal, :ar, :en, :tax, :cr, :contact, :phone, :email, 'ACTIVE', :cooldown, :threshold,
					        :now, :actor, :now, :actor)
					""")
				.params(draftParams(draft))
				.param("id", id)
				.param("cooldown", cooldown)
				.param("threshold", lowBalanceThresholdPiastres)
				.param("now", ts(now))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw new ApiException(HttpStatus.CONFLICT, "TAX_ID_IN_USE", "Another company has this tax id", "taxId", null);
		}
		audit.record(actorOf(actor), "COMPANY_CREATED", "COMPANY", id, Scope.company(id),
				Map.of("cooldownMinutes", cooldown, "lowBalanceThresholdPiastres", lowBalanceThresholdPiastres));
		// Synchronous: listeners run here, in this transaction. A company without its wallet never commits.
		events.publishEvent(new CompanyCreated(id));
		return get(id);
	}

	@Transactional
	Company update(ZawedPrincipal actor, UUID id, Draft draft) {
		Company before = lock(id);
		try {
			jdbc.sql("""
					update companies set legal_name = :legal, name_ar = :ar, name_en = :en, tax_id = :tax,
					       commercial_registration = :cr, contact_name = :contact, contact_phone = :phone,
					       contact_email = :email, updated_at = :now, updated_by = :actor
					where id = :id
					""")
				.params(draftParams(draft))
				.param("id", id)
				.param("now", ts(clock.instant()))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw new ApiException(HttpStatus.CONFLICT, "TAX_ID_IN_USE", "Another company has this tax id", "taxId", null);
		}
		audit.record(actorOf(actor), "COMPANY_UPDATED", "COMPANY", id, Scope.company(id),
				Map.of("legalNameBefore", before.legalName()));
		return get(id);
	}

	/** Cooldown and low-balance threshold: financial control settings, Super Admin only. */
	@Transactional
	Company updatePolicy(ZawedPrincipal actor, UUID id, int cooldownMinutes, long lowBalanceThresholdPiastres,
			String reason) {
		Company before = lock(id);
		jdbc.sql("""
				update companies set cooldown_minutes = :cooldown, low_balance_threshold_piastres = :threshold,
				       updated_at = :now, updated_by = :actor
				where id = :id
				""")
			.param("id", id)
			.param("cooldown", cooldownMinutes)
			.param("threshold", lowBalanceThresholdPiastres)
			.param("now", ts(clock.instant()))
			.param("actor", actor.userId())
			.update();
		Map<String, Object> change = new HashMap<>();
		change.put("cooldownMinutes", Map.of("from", before.cooldownMinutes(), "to", cooldownMinutes));
		change.put("lowBalanceThresholdPiastres",
				Map.of("from", before.lowBalanceThresholdPiastres(), "to", lowBalanceThresholdPiastres));
		change.put("reason", reason);
		audit.record(actorOf(actor), "COMPANY_POLICY_CHANGED", "COMPANY", id, Scope.company(id), change);
		return get(id);
	}

	/** Freeze or unfreeze (PRD §6.3 Freeze Account). A frozen company's vehicles cannot fuel. */
	@Transactional
	Company setStatus(ZawedPrincipal actor, UUID id, Status status, String reason) {
		Company before = lock(id);
		if (before.status() == status) {
			return before;
		}
		jdbc.sql("update companies set status = :status, updated_at = :now, updated_by = :actor where id = :id")
			.param("id", id)
			.param("status", status.name())
			.param("now", ts(clock.instant()))
			.param("actor", actor.userId())
			.update();
		audit.record(actorOf(actor), status == Status.FROZEN ? "COMPANY_FROZEN" : "COMPANY_UNFROZEN", "COMPANY", id,
				Scope.company(id), Map.of("reason", reason));
		return get(id);
	}

	@Transactional
	Department addDepartment(ZawedPrincipal actor, UUID companyId, String name, Long monthlyBudgetPiastres) {
		get(companyId);
		UUID id = ids.next();
		Instant now = clock.instant();
		try {
			jdbc.sql("""
					insert into departments (id, company_id, name, monthly_budget_piastres, status, created_at, created_by,
					                         updated_at, updated_by)
					values (:id, :company, :name, :budget, 'ACTIVE', :now, :actor, :now, :actor)
					""")
				.param("id", id)
				.param("company", companyId)
				.param("name", clean(name))
				.param("budget", monthlyBudgetPiastres)
				.param("now", ts(now))
				.param("actor", actor.userId())
				.update();
		}
		catch (DuplicateKeyException ex) {
			throw new ApiException(HttpStatus.CONFLICT, "DEPARTMENT_EXISTS", "A department with this name exists", "name",
					null);
		}
		audit.record(actorOf(actor), "DEPARTMENT_CREATED", "DEPARTMENT", id, Scope.company(companyId),
				Map.of("name", clean(name)));
		return jdbc.sql("select * from departments where id = :id").param("id", id).query(Department::map).single();
	}

	private static Map<String, Object> draftParams(Draft draft) {
		Map<String, Object> params = new HashMap<>();
		params.put("legal", clean(draft.legalName()));
		params.put("ar", clean(draft.nameAr()));
		params.put("en", clean(draft.nameEn()));
		params.put("tax", clean(draft.taxId()));
		params.put("cr", clean(draft.commercialRegistration()));
		params.put("contact", clean(draft.contactName()));
		params.put("phone", clean(draft.contactPhone()));
		String email = clean(draft.contactEmail());
		params.put("email", email == null ? null : email.toLowerCase());
		return params;
	}

	static String like(String query) {
		String cleaned = clean(query);
		if (cleaned == null) {
			return null;
		}
		return "%" + cleaned.toLowerCase().replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%";
	}

	private static AuditLog.Actor actorOf(ZawedPrincipal actor) {
		return new AuditLog.Actor(actor.userId(), actor.role());
	}

}
