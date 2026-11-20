package com.zawed.api.platform.crypto;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.util.HexFormat;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.stereotype.Component;

import com.zawed.api.platform.config.ZawedProperties;

/**
 * A keyed, deterministic hash for finding a secret value without storing it: an RFID tag UID is
 * stored encrypted (random nonce, so not searchable) plus this HMAC (searchable and unique).
 *
 * <p>Unlike a plain SHA-256, the HMAC cannot be reversed by hashing every possible UID: without the
 * key, a leaked table gives nothing. Each namespace derives its own key, so a tag hash cannot be
 * compared with any other kind of hash.
 *
 * <p>The master key is {@code zawed.security.lookup-key}, or the data key while that is key 1
 * (ADR 0027). It does not follow a data-key rotation: stored hashes (tag UIDs, activation codes),
 * receipt links already sent and the tag digest keys devices hold offline were all made with it.
 */
@Component
public class LookupHasher {

	private final byte[] masterKey;

	@Autowired
	public LookupHasher(ZawedProperties properties) {
		this(properties.security().lookupKeyBytes());
	}

	LookupHasher(byte[] masterKey) {
		this.masterKey = masterKey.clone();
	}

	public String hash(String namespace, String value) {
		byte[] key = hmac(masterKey, ("zawed-lookup:" + namespace).getBytes(StandardCharsets.UTF_8));
		return HexFormat.of().formatHex(hmac(key, value.getBytes(StandardCharsets.UTF_8)));
	}

	private static byte[] hmac(byte[] key, byte[] data) {
		try {
			Mac mac = Mac.getInstance("HmacSHA256");
			mac.init(new SecretKeySpec(key, "HmacSHA256"));
			return mac.doFinal(data);
		}
		catch (GeneralSecurityException ex) {
			throw new IllegalStateException(ex);
		}
	}

}
