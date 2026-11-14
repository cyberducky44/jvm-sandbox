# jvm-sandbox

Runs Minecraft (or any JVM) in an operating-system sandbox, so a malicious mod can't read
your browser cookies, Discord tokens, wallets, SSH keys or documents, and can't install
itself to run at startup.

The same command line works on both platforms:

| | Windows | Linux |
| --- | --- | --- |
| Mechanism | AppContainer | bubblewrap (namespaces) |
| Launcher | `bin\jvm-sandboxw.exe` | `./jvm-sandbox` |
| Needs | nothing extra | the `bubblewrap` package |

The kernel enforces the sandbox on the process, not anything inside the JVM. The JVM runs
unmodified: no agent, no bytecode rewriting, no SecurityManager, so there is no runtime
performance cost, and native code (JNI) is confined just the same as Java code.

## Install

**Windows:** run `build.cmd`. It uses the C# compiler that ships with Windows; no SDK needed.
Output: `bin\jvm-sandbox.exe` (console, for terminals) and `bin\jvm-sandboxw.exe` (no console
window, for launchers).

**Linux:** nothing to build. Install bubblewrap (`apt install bubblewrap`,
`dnf install bubblewrap`, `pacman -S bubblewrap`) and use the `jvm-sandbox` script.

## Use

    jvm-sandbox [options] [--] <java> [java args...]

**Lunar Client** (tested on Windows with 26.3 + Fabric): turn on Advanced Mode (the shield
next to the settings search), then set *Wrapper Command*. On Windows use forward slashes,
because Lunar's parser eats backslashes:

    C:/path/to/jvm-sandboxw.exe
    /path/to/jvm-sandbox

**Prism Launcher / MultiMC** (untested): Settings → Custom commands → *Wrapper command*,
set to the launcher's path.

**Anything else:** put the launcher in front of the Java command line.

| Option | Effect |
| --- | --- |
| `--rw <path>` | extra path the game may read and write |
| `--ro <path>` | extra path the game may read |
| `--deny <path>` | path the game must not read, even inside a granted folder |
| `--no-clipboard` | Windows: the game can't read or change the clipboard (breaks paste in chat). Linux: hides the X11 display, so it needs a Wayland session |
| `--no-net` | no network at all |
| `--lan` | Windows: also allow LAN connections and listening. Linux: no effect, see Limits |
| `--name <id>` | Windows: separate sandbox identity, so instances can't read each other. Linux: not needed, every launch only sees its own folders |
| `--no-elevate` | Windows: never show the one-time admin prompt |
| `--verbose` | print every permission that gets granted |
| `--log <file>` | also append the wrapper's messages to a file |

## What the game can touch

Granted automatically, worked out from the command line and environment:

- read: the Java install, everything on the classpath / module path, `-jar`, `-javaagent`,
  `java.library.path`, `--assetsDir`, and the launcher's `libraries` and `assets` folders
- read + write: `--gameDir`, `$INST_DIR` (the Prism/MultiMC instance), `~/.lunarclient` when
  launched by Lunar, and a private temp folder
- outbound internet (unless `--no-net`)

Everything else in your user profile or home directory is denied or simply not there, as is
writing to the registry or to any startup location. On Linux the game gets an empty throwaway
home, and no session D-Bus (keyring, secret service), ssh-agent or gpg-agent.

Some spots are blocked even though a parent folder is readable or writable:

- the official launcher's saved logins (`launcher_accounts*`, `launcher_msa_credentials*`) in
  the game directory can't be read
- the Java install can't be written to, so a mod can't plant code there
- Windows: Steam's login files (`Program Files (x86)\Steam\config`) can't be read; Windows lets
  every AppContainer read Program Files by default
- anything you pass to `--deny`

### Windows specifics

The first run adds permissions to the granted folders. Parent folders of those paths get a
"list names only" permission, because Java resolves paths by looking each component up in its
parent. For `C:\` and `C:\Users` that takes one admin prompt, once.

Protected spots are done by giving the path its own permission list without the sandbox on
it. Deny entries don't work: Windows ignores them for AppContainer identities.

### Linux specifics

Nothing on disk is changed; the sandbox is a set of mounts that exists only while the game
runs. The system (`/usr`, `/etc`, `/opt`) is visible read-only.

## Limits

- A mod runs inside the game, so it can still use the game's own session token and anything
  in the instance folder. Don't point the game directory at a folder holding other secrets.
- A mod can take screenshots of your whole screen while the game runs, and on Windows read
  the clipboard unless you pass `--no-clipboard`. On Linux under X11 any program that can show
  a window can also screenshot, read the clipboard and log keys; Wayland prevents that. Don't
  have a seed phrase on screen or in the clipboard while playing untrusted mods.
- A mod can still modify other files in the game folder (other mods, jars). Keep launching
  through the sandbox; a tampered file would run unsandboxed if you stop.
- Windows: the sandbox can see file and folder *names* (not contents) in the parent folders of
  granted paths, e.g. the top level of your user folder. Other programs installed under
  Program Files are readable; use `--deny` for any that keep secrets next to the program.
- Windows: connections to `localhost` are blocked. With Lunar this cuts the link between the
  game and the launcher window; the game itself, login and Lunar's online services work.
- Linux: network is all or nothing. With network on, the game can also reach `localhost` and
  your LAN, so services listening only on localhost are exposed to it.
- The official Minecraft launcher has no wrapper-command setting, so it can't be used directly.
- The Linux version is tested with the probe script in WSL, not yet with a real game.

## Test

Windows, `test\Probe.java` checks from inside the sandbox what is allowed and what is denied:

    cd <some empty dir containing Probe.java>
    jvm-sandbox --rw . -- java Probe.java . C:\path\to\a\file\outside

Windows, `test\StealerProbe.java` tries what an info-stealer would: Discord's token store,
browser passwords and cookies, wallet folders, SSH keys, Steam, reading or injecting into
another process (pass its pid), Discord's local ports, clipboard, screenshots, registry. It
only reports allowed or denied; it never reads out, decrypts or sends anything. Run it once
normally and once through the sandbox and compare (needs Java 22+):

    java --enable-native-access=ALL-UNNAMED -Dnowrite=1 StealerProbe.java <discord pid>
    jvm-sandbox -- java --enable-native-access=ALL-UNNAMED StealerProbe.java <discord pid>

Linux, `test/probe.sh` does the equivalent with no Java needed:

    ./jvm-sandbox --rw <dir> --ro test -- bash test/probe.sh <dir> <a file outside the sandbox>
