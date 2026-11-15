package com.zawed.api.platform.web;

/**
 * The API contract version. A client (the web app, later the POS) reads it from {@code /api/v1/meta}
 * and compares it with the version it was built for; an older POS build that sees a newer number
 * keeps queuing transactions instead of failing (PRD §2.2). It increases only on a change an
 * existing client cannot ignore.
 */
public final class ApiContract {

	public static final int VERSION = 1;

	public static final String BASE_PATH = "/api/v1";

	/**
	 * The POS protocol version (B5 contract §1), sent by a terminal as {@code X-Zawed-Contract}. A
	 * terminal below {@link #POS_CONTRACT_MINIMUM} is told to update before it may sync.
	 */
	public static final int POS_CONTRACT = 1;

	public static final int POS_CONTRACT_MINIMUM = 1;

	public static final String POS_CONTRACT_HEADER = "X-Zawed-Contract";

	private ApiContract() {
	}

}
