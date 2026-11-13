package com.zawed.api.platform.security;

import java.lang.annotation.Documented;
import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

import org.springframework.security.access.prepost.PreAuthorize;

/**
 * The permission an endpoint needs. Typed, so a misspelt permission does not compile.
 * Resolved through the template defaults declared in {@code SecurityConfig}.
 */
@Target({ ElementType.METHOD, ElementType.TYPE })
@Retention(RetentionPolicy.RUNTIME)
@Documented
@PreAuthorize("hasAuthority('PERM_{value}')")
public @interface RequiresPermission {

	Permission value();

}
