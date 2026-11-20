package com.zawed.api.platform.security;

import java.lang.annotation.Documented;
import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

import org.springframework.security.access.prepost.PreAuthorize;

/**
 * The permission an endpoint needs. Typed, so a misspelt permission does not compile.
 * Resolved through the template defaults declared in {@code SecurityConfig}, and checked a first
 * time before the request's arguments are read ({@code PermissionFirst}).
 */
@Target({ ElementType.METHOD, ElementType.TYPE })
@Retention(RetentionPolicy.RUNTIME)
@Documented
@PreAuthorize("hasAuthority('PERM_{value}')")
public @interface RequiresPermission {

	Permission value();

	/**
	 * When not empty, the caller's kind of scope must also be one of these. For a permission that
	 * several portals share (TICKETS_OPEN is held by company and station users), so a station manager
	 * is refused on the company portal's writes before the body is read ({@code PermissionFirst}).
	 */
	ScopeType[] scope() default {};

}
