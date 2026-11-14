import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.channels.Selector;
import java.nio.file.Files;
import java.nio.file.Path;

// Run inside the sandbox: args = <allowed dir> <forbidden file>
public class Probe {
    static void check(String what, boolean expectOk, Runnable r) {
        boolean ok;
        String err = "";
        try { r.run(); ok = true; } catch (Throwable t) { ok = false; err = " (" + t + ")"; }
        System.out.println((ok == expectOk ? "PASS " : "FAIL ") + what + ": " + (ok ? "allowed" : "denied") + err);
    }

    interface Body { void run() throws Exception; }
    static Runnable r(Body b) { return () -> { try { b.run(); } catch (Exception e) { throw new RuntimeException(e); } }; }

    public static void main(String[] a) {
        Path allowed = Path.of(a[0]), secret = Path.of(a[1]);
        boolean net = a.length < 3 || !a[2].equals("nonet");
        check("write game dir", true, r(() -> Files.writeString(allowed.resolve("probe.txt"), "hi")));
        check("read game dir", true, r(() -> Files.readString(allowed.resolve("probe.txt"))));
        check("temp file", true, r(() -> Files.delete(Files.createTempFile("probe", ".tmp"))));
        check("nio selector", true, r(() -> Selector.open().close()));
        check("read secret outside sandbox", false, r(() -> Files.readString(secret)));
        check("overwrite secret", false, r(() -> Files.writeString(secret, "pwned")));
        check("write startup folder", false, r(() -> Files.writeString(Path.of(System.getenv("APPDATA"),
                "Microsoft/Windows/Start Menu/Programs/Startup/probe.cmd"), "rem")));
        check("registry persistence (HKCU Run)", false, r(() -> {
            Process p = new ProcessBuilder("reg", "add", "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                    "/v", "jvmsandboxprobe", "/d", "x", "/f").redirectErrorStream(true).start();
            p.getInputStream().readAllBytes();
            if (p.waitFor() != 0) throw new RuntimeException("reg exit " + p.exitValue());
        }));
        check("internet", net, r(() -> { try (Socket s = new Socket()) { s.connect(new InetSocketAddress("example.com", 80), 5000); } }));
    }
}
