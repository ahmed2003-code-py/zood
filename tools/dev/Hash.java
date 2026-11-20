import org.springframework.security.crypto.argon2.Argon2PasswordEncoder;

/** Same parameters as com.zawed.api.platform.crypto.PasswordHasher (Argon2id, 16-byte salt, 32-byte hash, 1 lane, 19 MiB, 2 iterations). */
public class Hash {
	public static void main(String[] args) {
		String secret = System.getenv("NEW_PASSWORD");
		Argon2PasswordEncoder encoder = new Argon2PasswordEncoder(16, 32, 1, 19 * 1024, 2);
		String hash = encoder.encode(secret);
		if (!encoder.matches(secret, hash) || encoder.matches(secret + "x", hash)) {
			throw new IllegalStateException("self-check failed");
		}
		System.out.println(hash);
	}
}
