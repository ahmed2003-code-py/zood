import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.HexFormat;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import org.springframework.security.crypto.argon2.Argon2PasswordEncoder;

/** Golden vectors written by the Java API's own algorithms (copied verbatim from platform.crypto). */
public class Golden {
  static byte[] hmac(byte[] key, byte[] data) throws Exception { Mac m = Mac.getInstance("HmacSHA256"); m.init(new SecretKeySpec(key, "HmacSHA256")); return m.doFinal(data); }
  static String lookup(byte[] master, String ns, String value) throws Exception {
    byte[] key = hmac(master, ("zawed-lookup:" + ns).getBytes(StandardCharsets.UTF_8));
    return HexFormat.of().formatHex(hmac(key, value.getBytes(StandardCharsets.UTF_8)));
  }
  static byte[] encrypt(int keyId, byte[] key, byte[] plain, byte[] aad) throws Exception {
    byte[] nonce = new byte[12]; new java.security.SecureRandom().nextBytes(nonce);
    Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
    c.init(Cipher.ENCRYPT_MODE, new SecretKeySpec(key, "AES"), new GCMParameterSpec(128, nonce));
    c.updateAAD(new byte[] { (byte) keyId }); c.updateAAD(aad);
    byte[] sealed = c.doFinal(plain);
    return ByteBuffer.allocate(1 + 12 + sealed.length).put((byte) keyId).put(nonce).put(sealed).array();
  }
  public static void main(String[] a) throws Exception {
    Argon2PasswordEncoder enc = new Argon2PasswordEncoder(16, 32, 1, 19 * 1024, 2);
    System.out.println("argon2.password=Zawed-Pass-2026! كلمة");
    System.out.println("argon2.hash=" + enc.encode("Zawed-Pass-2026! كلمة"));
    byte[] key1 = Base64.getDecoder().decode("bG9jYWwtb25seS1kYXRhLWtleS1ub3QtZm9yLXByb2Q=");
    byte[] key200 = new byte[32]; for (int i = 0; i < 32; i++) key200[i] = (byte) (i * 7 + 3);
    byte[] aad = "user:0193a5b2-7c41-7000-8000-000000000001".getBytes(StandardCharsets.UTF_8);
    System.out.println("aes.key1.value=" + Base64.getEncoder().encodeToString(encrypt(1, key1, "JBSWY3DPEHPK3PXP".getBytes(StandardCharsets.UTF_8), aad)));
    System.out.println("aes.key200=" + Base64.getEncoder().encodeToString(key200));
    System.out.println("aes.key200.value=" + Base64.getEncoder().encodeToString(encrypt(200, key200, "EG-NBE 0001234567".getBytes(StandardCharsets.UTF_8), aad)));
    System.out.println("lookup.tag=" + lookup(key1, "tag-uid", "04A1B2C3D4E5F6"));
    System.out.println("lookup.fingerprint=" + lookup(key1, "lookup-key-fingerprint", "zawed"));
  }
}
