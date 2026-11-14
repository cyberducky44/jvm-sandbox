import java.awt.Robot;
import java.awt.Rectangle;
import java.awt.Toolkit;
import java.awt.datatransfer.DataFlavor;
import java.awt.image.BufferedImage;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.lang.foreign.Arena;
import java.lang.foreign.FunctionDescriptor;
import java.lang.foreign.Linker;
import java.lang.foreign.MemorySegment;
import java.lang.foreign.SymbolLookup;
import java.lang.foreign.ValueLayout;
import java.lang.invoke.MethodHandle;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.file.AccessDeniedException;
import java.nio.file.FileVisitResult;
import java.nio.file.Files;
import java.nio.file.NoSuchFileException;
import java.nio.file.Path;
import java.nio.file.SimpleFileVisitor;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.EnumSet;

/**
 * Checks what an info-stealer could reach from where it runs. For every target it only records
 * whether access is possible. Nothing is parsed, decrypted, printed, stored or sent anywhere.
 *
 * Run it once normally and once through jvm-sandbox and compare.
 *   java StealerProbe.java [pid of a process to try to open, e.g. Discord] ...
 */
public class StealerProbe {
    // Not %LOCALAPPDATA%: Windows points that at a private folder inside an AppContainer.
    static final String HOME = System.getenv("USERPROFILE"), ROAMING = HOME + "/AppData/Roaming", LOCAL = HOME + "/AppData/Local";

    static void out(String category, String what, String result) {
        System.out.printf("%-10s | %-46s | %s%n", category, what, result);
    }

    /** Tries to open a file, or up to 300 files below a directory, reading one byte from each. */
    static void files(String category, String what, String path) {
        Path p = Path.of(path);
        final int[] n = new int[3]; // opened, denied, locked
        try {
            BasicFileAttributes a = Files.readAttributes(p, BasicFileAttributes.class);
            if (!a.isDirectory()) open(p, n);
            else Files.walkFileTree(p, EnumSet.noneOf(java.nio.file.FileVisitOption.class), 4, new SimpleFileVisitor<Path>() {
                public FileVisitResult visitFile(Path f, BasicFileAttributes at) {
                    if (at.isRegularFile()) open(f, n);
                    return n[0] + n[1] + n[2] >= 300 ? FileVisitResult.TERMINATE : FileVisitResult.CONTINUE;
                }
                public FileVisitResult visitFileFailed(Path f, IOException e) { n[1]++; return FileVisitResult.CONTINUE; }
            });
        } catch (NoSuchFileException e) { out(category, what, "not present"); return;
        } catch (AccessDeniedException e) { out(category, what, "DENIED"); return;
        } catch (IOException e) { out(category, what, "DENIED (" + e.getClass().getSimpleName() + ")"); return; }
        if (n[0] > 0) out(category, what, "READABLE  (" + n[0] + " file(s) opened" + (n[1] > 0 ? ", " + n[1] + " denied" : "") + (n[2] > 0 ? ", " + n[2] + " locked" : "") + ")");
        else if (n[2] > 0) out(category, what, "VISIBLE   (" + n[2] + " file(s) there but locked by the running app)");
        else if (n[1] > 0) out(category, what, "DENIED");
        else out(category, what, "empty");
    }

    static void open(Path f, int[] n) {
        try (RandomAccessFile r = new RandomAccessFile(f.toFile(), "r")) { r.read(); n[0]++; }
        catch (IOException e) {
            String m = String.valueOf(e.getMessage());
            if (m.contains("denied")) n[1]++; else n[2]++;
        }
    }

    interface Check { String run() throws Throwable; }
    static void check(String category, String what, Check c) {
        String r;
        try { r = c.run(); } catch (Throwable t) { r = "DENIED (" + t.getClass().getSimpleName() + ": " + String.valueOf(t.getMessage()).replace('\n', ' ') + ")"; }
        out(category, what, r);
    }

    static String run(String... cmd) throws Exception {
        Process p = new ProcessBuilder(cmd).redirectErrorStream(true).start();
        try (InputStream in = p.getInputStream()) { in.readAllBytes(); }
        return p.waitFor() == 0 ? "ALLOWED" : "DENIED (exit " + p.exitValue() + ")";
    }

    public static void main(String[] args) throws Throwable {
        // --- Discord ---
        for (String d : new String[] { "discord", "discordcanary", "discordptb" }) {
            files("discord", d + " token store (leveldb)", ROAMING + "\\" + d + "\\Local Storage\\leveldb");
            files("discord", d + " encryption key (Local State)", ROAMING + "\\" + d + "\\Local State");
        }
        // --- browsers: saved passwords, cookies, extension wallets ---
        String[][] chromium = { { "Chrome", LOCAL + "\\Google\\Chrome\\User Data" }, { "Edge", LOCAL + "\\Microsoft\\Edge\\User Data" },
                { "Brave", LOCAL + "\\BraveSoftware\\Brave-Browser\\User Data" }, { "Opera", ROAMING + "\\Opera Software\\Opera Stable" } };
        for (String[] b : chromium) {
            files("browser", b[0] + " key (Local State)", b[1] + "\\Local State");
            files("browser", b[0] + " saved passwords", b[1] + "\\Default\\Login Data");
            files("browser", b[0] + " cookies", b[1] + "\\Default\\Network\\Cookies");
            files("wallet", b[0] + " extension wallets (MetaMask etc.)", b[1] + "\\Default\\Local Extension Settings");
        }
        files("browser", "Firefox profiles (logins, cookies)", ROAMING + "\\Mozilla\\Firefox\\Profiles");
        // --- desktop wallets ---
        String[][] wallets = { { "Exodus", ROAMING + "\\Exodus" }, { "Electrum", ROAMING + "\\Electrum\\wallets" }, { "Atomic", ROAMING + "\\atomic\\Local Storage" },
                { "Bitcoin Core", ROAMING + "\\Bitcoin" }, { "Ethereum keystore", ROAMING + "\\Ethereum\\keystore" }, { "Ledger Live", ROAMING + "\\Ledger Live" },
                { "Monero", HOME + "\\Documents\\Monero" }, { "Coinomi", LOCAL + "\\Coinomi" } };
        for (String[] w : wallets) files("wallet", w[0], w[1]);
        // --- other things stealers grab ---
        files("other", "SSH keys", HOME + "\\.ssh");
        files("other", "Git credentials", HOME + "\\.git-credentials");
        files("other", "AWS credentials", HOME + "\\.aws");
        files("other", "Windows Credential Manager", LOCAL + "\\Microsoft\\Credentials");
        files("other", "Telegram session", ROAMING + "\\Telegram Desktop\\tdata");
        files("other", "Steam login files", "C:\\Program Files (x86)\\Steam\\config");
        files("other", "Desktop", HOME + "\\Desktop");
        files("other", "Documents", HOME + "\\Documents");
        files("other", "Downloads", HOME + "\\Downloads");
        // --- Minecraft accounts ---
        files("minecraft", "official launcher saved login", ROAMING + "\\.minecraft\\launcher_msa_credentials_microsoft_store.bin");
        files("minecraft", "official launcher accounts", ROAMING + "\\.minecraft\\launcher_accounts_microsoft_store.json");
        files("minecraft", "Lunar accounts (game session)", HOME + "\\.lunarclient\\settings\\game\\accounts.json");

        check("minecraft", "plant a file in the Java install", () -> {
            Path f = Path.of(System.getProperty("java.home"), "bin", "sbx_probe.tmp");
            Files.writeString(f, "x"); Files.delete(f); return "ALLOWED";
        });

        // --- not files ---
        Linker linker = Linker.nativeLinker();
        MethodHandle openProcess = linker.downcallHandle(SymbolLookup.libraryLookup("kernel32", Arena.global()).find("OpenProcess").orElseThrow(),
                FunctionDescriptor.of(ValueLayout.ADDRESS, ValueLayout.JAVA_INT, ValueLayout.JAVA_INT, ValueLayout.JAVA_INT));
        for (String a : args) {
            if (!a.matches("[0-9]+")) continue;
            int pid = Integer.parseInt(a);
            check("process", "read memory of pid " + pid, () -> ((MemorySegment) openProcess.invoke(0x0410, 0, pid)).address() != 0 ? "ALLOWED" : "DENIED");
            check("process", "inject code into pid " + pid, () -> ((MemorySegment) openProcess.invoke(0x043A, 0, pid)).address() != 0 ? "ALLOWED" : "DENIED");
        }
        check("process", "see other running programs", () -> {
            long n = ProcessHandle.allProcesses().filter(p -> p.info().command().isPresent()).count();
            return n > 3 ? "ALLOWED (" + n + " with visible path)" : "DENIED (" + n + " visible)";
        });
        check("discord", "local RPC port 6463", () -> { try (Socket s = new Socket()) { s.connect(new InetSocketAddress("127.0.0.1", 6463), 2000); return "ALLOWED"; } });
        check("discord", "local IPC pipe discord-ipc-0", () -> { try (RandomAccessFile r = new RandomAccessFile("\\\\.\\pipe\\discord-ipc-0", "rw")) { return "ALLOWED"; } });
        check("desktop", "read clipboard", () -> {
            Object o = Toolkit.getDefaultToolkit().getSystemClipboard().getData(DataFlavor.stringFlavor);
            return "ALLOWED (got " + String.valueOf(o).length() + " characters)";
        });
        check("desktop", "take screenshot", () -> {
            BufferedImage img = new Robot().createScreenCapture(new Rectangle(Toolkit.getDefaultToolkit().getScreenSize()));
            int first = img.getRGB(0, 0); boolean blank = true;
            for (int y = 0; y < img.getHeight() && blank; y += 37) for (int x = 0; x < img.getWidth(); x += 41) if (img.getRGB(x, y) != first) { blank = false; break; }
            return blank ? "DENIED (blank image)" : "ALLOWED (" + img.getWidth() + "x" + img.getHeight() + ", real content)";
        });
        check("registry", "read HKCU (user settings)", () -> run("reg", "query", "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"));
        if (System.getProperty("nowrite") == null) check("registry", "write HKCU autostart", () -> {
            String r = run("reg", "add", "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", "/v", "jvmsandboxprobe", "/d", "x", "/f");
            if (r.startsWith("ALLOWED")) run("reg", "delete", "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", "/v", "jvmsandboxprobe", "/f");
            return r;
        });
        check("network", "send data out to the internet", () -> { try (Socket s = new Socket()) { s.connect(new InetSocketAddress("example.com", 443), 5000); return "ALLOWED"; } });
        System.exit(0);
    }
}
