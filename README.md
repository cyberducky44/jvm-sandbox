# jvm-sandbox

Runs Minecraft (or any JVM) inside a Windows **AppContainer**, so a malicious mod can't
read your browser cookies, Discord tokens, SSH keys or documents, and can't install itself
to run at startup.

The sandbox is enforced by the Windows kernel on the process, not by anything inside the JVM.
The JVM runs unmodified: no agent, no bytecode rewriting, no SecurityManager, so there is no
runtime performance cost, and native code (JNI) is confined just the same as Java code.

## Build

    build.cmd

Uses the C# compiler that ships with Windows; no SDK needed. Output: `bin\jvm-sandbox.exe`
(console, for terminals) and `bin\jvm-sandboxw.exe` (no console window, for launchers).

## Use

    jvm-sandbox [options] [--] <java.exe> [java args...]

**Lunar Client** (tested with 26.3 + Fabric): turn on Advanced Mode (the shield next to the
settings search), then set *Wrapper Command*. Use forward slashes, because Lunar's parser
eats backslashes:

    C:/path/to/jvm-sandboxw.exe

**Prism Launcher / MultiMC** (untested): Settings → Custom commands → *Wrapper command*:

    C:\path\to\jvm-sandboxw.exe

**Anything else:** put `jvm-sandbox.exe` in front of the Java command line.

| Option | Effect |
| --- | --- |
| `--rw <path>` | extra path the game may read and write |
| `--ro <path>` | extra path the game may read |
| `--name <id>` | separate sandbox identity, so instances can't read each other |
| `--deny <path>` | path the game must not read, even inside a granted folder |
| `--no-clipboard` | the game can't read or change the clipboard (breaks paste in chat) |
| `--no-net` | no network at all |
| `--lan` | also allow LAN connections and listening (LAN worlds, hosting) |
| `--no-elevate` | never show the one-time admin prompt |
| `--verbose` | print every permission that gets granted |
| `--log <file>` | also append the wrapper's messages to a file |

## What the game can touch

Granted automatically, worked out from the command line and environment:

- read: the Java install, everything on the classpath / module path, `-jar`, `-javaagent`,
  `java.library.path`, `--assetsDir`, and the launcher's `libraries` and `assets` folders
- read + write: `--gameDir`, `%INST_DIR%` (the Prism/MultiMC instance), `~/.lunarclient` when
  launched by Lunar, and a private temp folder
- outbound internet (unless `--no-net`)

Everything else in your user profile and on other drives is denied, as is writing to the
registry or to any startup location.

Some spots are blocked even though a parent folder is readable or writable:

- the official launcher's saved logins (`launcher_accounts*`, `launcher_msa_credentials*`) in
  the game directory can't be read
- the Java install can't be written to, so a mod can't plant code there
- Steam's login files (`Program Files (x86)\Steam\config`) can't be read; Windows lets every
  AppContainer read Program Files by default
- anything you pass to `--deny`

These are done by giving the path its own permission list without the sandbox on it. Deny
entries don't work: Windows ignores them for AppContainer identities.

The first run adds permissions to the folders above. Parent folders of those paths get a
"list names only" permission, because Java resolves paths by looking each component up in its
parent. For `C:\` and `C:\Users` that takes one admin prompt, once.

## Limits

- A mod runs inside the game, so it can still use the game's own session token and anything
  in the instance folder. Don't point the game directory at a folder holding other secrets.
- A mod can take screenshots of your whole screen while the game runs, and read the clipboard
  unless you pass `--no-clipboard`. Don't have a seed phrase on screen or in the clipboard
  while playing untrusted mods.
- Other programs installed under Program Files are readable. Their secrets normally live in
  your user profile (blocked), but use `--deny` for any that keep them next to the program.
- A mod can still modify other files in the game folder (other mods, jars). Keep launching
  through the sandbox; a tampered file would run unsandboxed if you stop.
- The sandbox can see file and folder *names* (not contents) in the parent folders of granted
  paths, e.g. the top level of your user folder.
- Connections to `localhost` are blocked by Windows for AppContainers. With Lunar this cuts
  the link between the game and the launcher window; the game itself, login and Lunar's
  online services work.
- Windows only. The official Minecraft launcher has no wrapper-command setting, so it can't
  be used directly.

## Test

`test\Probe.java` checks from inside the sandbox what is allowed and what is denied:

    cd <some empty dir containing Probe.java>
    jvm-sandbox --rw . -- java Probe.java . C:\path\to\a\file\outside

`test\StealerProbe.java` tries what an info-stealer would: Discord's token store, browser
passwords and cookies, wallet folders, SSH keys, Steam, reading or injecting into another
process (pass its pid), Discord's local ports, clipboard, screenshots, registry. It only
reports allowed or denied; it never reads out, decrypts or sends anything. Run it once
normally and once through the sandbox and compare (needs Java 22+):

    java --enable-native-access=ALL-UNNAMED -Dnowrite=1 StealerProbe.java <discord pid>
    jvm-sandbox -- java --enable-native-access=ALL-UNNAMED StealerProbe.java <discord pid>
