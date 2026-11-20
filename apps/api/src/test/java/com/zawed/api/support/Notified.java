package com.zawed.api.support;

import static org.assertj.core.api.Assertions.assertThat;

import java.net.http.HttpResponse;
import java.util.List;
import java.util.Map;

import com.jayway.jsonpath.JsonPath;

import com.zawed.api.identity.IdentityTestSupport;
import com.zawed.api.identity.IdentityTestSupport.SignedIn;

/**
 * Reads a signed-in user's notifications through the API (B7 W5), the way the web bell will. A
 * platform user shares the platform's notifications with every earlier test, so assertions about
 * platform roles look at the notifications about one entity; a fresh company or operator starts
 * with none, so its count can be asserted exactly.
 */
public final class Notified {

	private Notified() {
	}

	/** The user's newest notifications (up to 100), optionally only the unread ones. */
	public static List<Map<String, Object>> list(IdentityTestSupport test, SignedIn user, boolean unreadOnly)
			throws Exception {
		HttpResponse<String> response = test.authed("GET",
				"/api/v1/notifications?limit=100" + (unreadOnly ? "&unread=true" : ""), user.accessToken(), null);
		assertThat(response.statusCode()).as(response.body()).isEqualTo(200);
		return JsonPath.read(response.body(), "$.items");
	}

	/** The types of the user's notifications that point at this entity, newest first. */
	public static List<String> typesAbout(IdentityTestSupport test, SignedIn user, Object entityId) throws Exception {
		return list(test, user, false).stream()
			.filter((n) -> entityId.toString().equals(n.get("entityId")))
			.map((n) -> (String) n.get("type"))
			.toList();
	}

	/** The one notification of this type about this entity that the user sees. */
	public static Map<String, Object> about(IdentityTestSupport test, SignedIn user, Object entityId, String type)
			throws Exception {
		List<Map<String, Object>> found = list(test, user, false).stream()
			.filter((n) -> entityId.toString().equals(n.get("entityId")) && type.equals(n.get("type")))
			.toList();
		assertThat(found).as("%s about %s", type, entityId).hasSize(1);
		return found.getFirst();
	}

	/** The unread count as the API reports it. */
	public static int unread(IdentityTestSupport test, SignedIn user) throws Exception {
		HttpResponse<String> response = test.authed("GET", "/api/v1/notifications/unread-count", user.accessToken(), null);
		assertThat(response.statusCode()).as(response.body()).isEqualTo(200);
		return JsonPath.<Integer>read(response.body(), "$.count");
	}

	/** The parameters of a notification as the API returned them. */
	@SuppressWarnings("unchecked")
	public static Map<String, Object> params(Map<String, Object> notification) {
		return (Map<String, Object>) notification.get("params");
	}

}
