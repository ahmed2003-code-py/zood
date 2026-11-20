package com.zawed.api.platform.web;

import static org.assertj.core.api.Assertions.assertThat;

import org.junit.jupiter.api.Test;

/** The subject of a per-address limit (U1 demo form): IPv4 as it is, IPv6 by its /64. */
class ClientInfoTests {

	@Test
	void anIpv4AddressIsLimitedAsItIs() {
		assertThat(ClientInfo.limitKeyOf("203.0.113.7")).isEqualTo("203.0.113.7");
		assertThat(ClientInfo.limitKeyOf("::ffff:203.0.113.7")).isEqualTo("203.0.113.7");
	}

	@Test
	void everyAddressOfOneIpv6Slash64SharesOneBudget() {
		String a = ClientInfo.limitKeyOf("2001:db8:1234:5678:0:0:0:1");
		String b = ClientInfo.limitKeyOf("2001:db8:1234:5678:abcd:ef01:2345:6789");
		String otherNetwork = ClientInfo.limitKeyOf("2001:db8:1234:5679::1");
		assertThat(a).isEqualTo(b).endsWith("/64");
		assertThat(otherNetwork).isNotEqualTo(a);
	}

	@Test
	void anIpv6ClientAlsoCountsInItsSlash48AndIpv4HasNoWiderNetwork() {
		String a = ClientInfo.networkKeyOf("2001:db8:1234:5678::1");
		String b = ClientInfo.networkKeyOf("2001:db8:1234:ffff:1:2:3:4");
		assertThat(a).isEqualTo(b).endsWith("/48");
		assertThat(ClientInfo.networkKeyOf("2001:db8:1235::1")).isNotEqualTo(a);
		assertThat(ClientInfo.networkKeyOf("203.0.113.7")).isNull();
		assertThat(ClientInfo.networkKeyOf("::ffff:203.0.113.7")).isNull();
		assertThat(ClientInfo.networkKeyOf("junk")).isNull();
	}

	@Test
	void anythingElseIsOneSharedUnknownBucket() {
		assertThat(ClientInfo.limitKeyOf(null)).isEqualTo("unknown");
		assertThat(ClientInfo.limitKeyOf("not an address")).isEqualTo("unknown");
	}

}
