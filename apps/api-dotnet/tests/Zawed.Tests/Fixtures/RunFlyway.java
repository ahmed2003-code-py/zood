import org.flywaydb.core.Flyway;
public class RunFlyway {
  public static void main(String[] a) {
    var f = Flyway.configure().dataSource(a[0], a[1], a[2]).locations("filesystem:" + a[3]);
    if (a.length > 4) f = f.target(a[4]);
    var r = f.load().migrate();
    System.out.println("applied=" + r.migrationsExecuted + " target=" + r.targetSchemaVersion);
  }
}
